#pragma once

#include "replacement_guard_status.h"

namespace ReplacementGuardReserve {
enum class Result : uint8_t { Held, ReceiptVerifiedHeld };

inline bool samePrepared(const EnrollmentJournal::Record& a, const EnrollmentJournal::Record& b) {
  return EnrollmentJournal::valid(a) && EnrollmentJournal::valid(b) &&
      a.kind == EnrollmentJournal::Kind::Replacement && b.kind == EnrollmentJournal::Kind::Replacement &&
      a.state == EnrollmentJournal::State::ReplacementPrepared &&
      b.state == EnrollmentJournal::State::ReplacementPrepared &&
      a.slot == b.slot &&
      memcmp(a.preparation, b.preparation, EnrollmentJournal::IdSize) == 0 &&
      memcmp(a.operation, b.operation, EnrollmentJournal::IdSize) == 0;
}

template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& pending, const char* nonce, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength) {
  char request[160];
  size_t requestLength = 0;
  if (!body || !tag || tagLength != ReplacementGuardStatus::TagSize ||
      pending.state != EnrollmentJournal::State::ReplacementPrepared ||
      !ReplacementGuardStatus::buildRequest(pending, nonce, request, sizeof(request), requestLength) ||
      bodyLength != requestLength + sizeof("guarded_only\n") - 1 ||
      memcmp(body, request, requestLength) != 0 ||
      memcmp(body + requestLength, "guarded_only\n", sizeof("guarded_only\n") - 1) != 0)
    return false;
  return verify(body, bodyLength, tag, tagLength);
}

// A reservation receipt is not a write grant. Every outcome leaves the journal held.
template <typename Storage, typename Adapter>
Result reserve(Storage& storage, Adapter& adapter, const EnrollmentJournal::Record& pending) {
  EnrollmentJournal::Record current{};
  if (!samePrepared(pending, pending) ||
      storage.read(current) != EnrollmentJournal::ReadStatus::Empty ||
      !storage.write(pending) ||
      storage.read(current) != EnrollmentJournal::ReadStatus::Valid || !samePrepared(pending, current))
    return Result::Held;

  char preparation[37], operation[37], nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[160];
  uint8_t challenge[ReplacementGuardStatus::ChallengeSize], requestTag[ReplacementGuardStatus::TagSize];
  uint8_t responseTag[ReplacementGuardStatus::TagSize], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!EnrollmentJournal::formatId(pending.preparation, preparation) ||
      !EnrollmentJournal::formatId(pending.operation, operation) ||
      !adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      nonce[EnrollmentRecovery::NonceHexLength] != '\0' ||
      !ReplacementGuardStatus::buildRequest(pending, nonce, request, sizeof(request), requestLength) ||
      !adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) ||
      !adapter.exchange(preparation, operation, pending.slot, nonce, challenge, requestTag,
                        response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response) ||
      !verifyResponse(pending, nonce,
          [&](const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
            return adapter.verify(challenge, body, length, tag, tagLength);
          }, response, responseLength, responseTag, sizeof(responseTag)) ||
      storage.read(current) != EnrollmentJournal::ReadStatus::Valid || !samePrepared(pending, current))
    return Result::Held;
  return Result::ReceiptVerifiedHeld;
}
}
