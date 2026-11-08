#pragma once

#include "enrollment_journal.h"
#include "enrollment_recovery.h"

namespace ReplacementWriteFence {
enum class Result : uint8_t { Held, FencedHeld };
constexpr size_t TagSize = 32;

inline bool fenced(const EnrollmentJournal::Record& record) {
  return EnrollmentJournal::valid(record) && record.kind == EnrollmentJournal::Kind::Replacement &&
      record.state == EnrollmentJournal::State::ReplacementFenced;
}

inline bool sameFenced(const EnrollmentJournal::Record& a, const EnrollmentJournal::Record& b) {
  return fenced(a) && fenced(b) && a.slot == b.slot &&
      memcmp(a.preparation, b.preparation, EnrollmentJournal::IdSize) == 0 &&
      memcmp(a.operation, b.operation, EnrollmentJournal::IdSize) == 0;
}

inline bool buildRequest(const EnrollmentJournal::Record& record, const char* nonce,
                         char* output, size_t capacity, size_t& length) {
  if (!fenced(record) || !nonce || strlen(nonce) != EnrollmentRecovery::NonceHexLength || !output) return false;
  for (size_t i = 0; i < EnrollmentRecovery::NonceHexLength; ++i)
    if (!((nonce[i] >= '0' && nonce[i] <= '9') || (nonce[i] >= 'a' && nonce[i] <= 'f'))) return false;
  char preparation[37], operation[37];
  if (!EnrollmentJournal::formatId(record.preparation, preparation) ||
      !EnrollmentJournal::formatId(record.operation, operation)) return false;
  int written = snprintf(output, capacity, "%s\n%s\n%u\n%s\nfenced\n",
      preparation, operation, static_cast<unsigned>(record.slot), nonce);
  if (written <= 0 || static_cast<size_t>(written) >= capacity) return false;
  length = static_cast<size_t>(written);
  return true;
}

template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& record, const char* nonce, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength) {
  char request[200];
  size_t requestLength = 0;
  if (!body || !tag || tagLength != TagSize ||
      !buildRequest(record, nonce, request, sizeof(request), requestLength) ||
      bodyLength != requestLength + sizeof("fenced_only\n") - 1 ||
      memcmp(body, request, requestLength) != 0 ||
      memcmp(body + requestLength, "fenced_only\n", sizeof("fenced_only\n") - 1) != 0)
    return false;
  return verify(body, bodyLength, tag, tagLength);
}

// Caller must serialize this routine with every sensor write and abandon any
// in-memory write grant. A failed operation never restores the old state.
template <typename Storage, typename Adapter>
Result fence(Storage& storage, Adapter& adapter) {
  EnrollmentJournal::Record record{};
  if (storage.read(record) != EnrollmentJournal::ReadStatus::Valid || !EnrollmentJournal::valid(record) ||
      record.kind != EnrollmentJournal::Kind::Replacement ||
      (record.state != EnrollmentJournal::State::ReplacementPrepared &&
       record.state != EnrollmentJournal::State::Uncertain &&
       record.state != EnrollmentJournal::State::ReplacementFenced))
    return Result::Held;
  if (!fenced(record)) {
    record.state = EnrollmentJournal::State::ReplacementFenced;
    if (!storage.write(record)) return Result::Held;
  }
  EnrollmentJournal::Record current{};
  if (storage.read(current) != EnrollmentJournal::ReadStatus::Valid || !sameFenced(record, current))
    return Result::Held;

  char preparation[37], operation[37], nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[200];
  uint8_t challenge[32], requestTag[TagSize], responseTag[TagSize], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!EnrollmentJournal::formatId(record.preparation, preparation) ||
      !EnrollmentJournal::formatId(record.operation, operation) ||
      !adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      nonce[EnrollmentRecovery::NonceHexLength] != '\0' ||
      !buildRequest(record, nonce, request, sizeof(request), requestLength) ||
      !adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) ||
      !adapter.exchange(preparation, operation, record.slot, nonce, challenge, requestTag,
                        response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response) ||
      !verifyResponse(record, nonce,
          [&](const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
            return adapter.verify(challenge, body, length, tag, tagLength);
          }, response, responseLength, responseTag, sizeof(responseTag)) ||
      storage.read(current) != EnrollmentJournal::ReadStatus::Valid || !sameFenced(record, current))
    return Result::Held;
  return Result::FencedHeld;
}
}
