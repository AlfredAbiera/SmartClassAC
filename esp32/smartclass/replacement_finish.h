#pragma once

#include "enrollment_journal.h"
#include "enrollment_recovery.h"

namespace ReplacementFinish {
constexpr size_t ChallengeSize = 32;
constexpr size_t TagSize = 32;
enum class Result : uint8_t { Completed, Held };

inline bool confirmed(const EnrollmentJournal::Record& record) {
  return EnrollmentJournal::valid(record) && record.kind == EnrollmentJournal::Kind::Replacement &&
         record.state == EnrollmentJournal::State::Confirmed;
}

inline bool buildRequest(const EnrollmentJournal::Record& record, const char* nonce,
                         char* output, size_t capacity, size_t& length) {
  if (!confirmed(record) || !nonce || strlen(nonce) != EnrollmentRecovery::NonceHexLength || !output)
    return false;
  for (size_t i = 0; i < EnrollmentRecovery::NonceHexLength; ++i)
    if (!((nonce[i] >= '0' && nonce[i] <= '9') || (nonce[i] >= 'a' && nonce[i] <= 'f'))) return false;
  char preparation[37], operation[37];
  if (!EnrollmentJournal::formatId(record.preparation, preparation) ||
      !EnrollmentJournal::formatId(record.operation, operation)) return false;
  int written = snprintf(output, capacity, "%s\n%s\n%u\n%s\nconfirmed\n", preparation, operation,
                         static_cast<unsigned>(record.slot), nonce);
  if (written <= 0 || static_cast<size_t>(written) >= capacity) return false;
  length = static_cast<size_t>(written);
  return true;
}

template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& record, const char* nonce, Verify verify,
                    const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
  char request[200];
  size_t requestLength = 0;
  if (!body || !tag || tagLength != TagSize ||
      !buildRequest(record, nonce, request, sizeof(request), requestLength) ||
      length != requestLength + sizeof("completed\n") - 1 ||
      memcmp(body, request, requestLength) != 0 ||
      memcmp(body + requestLength, "completed\n", sizeof("completed\n") - 1) != 0)
    return false;
  return verify(body, length, tag, tagLength);
}

template <typename Storage, typename Adapter>
Result finish(Storage& storage, Adapter& adapter) {
  EnrollmentJournal::Record pending{};
  if (storage.read(pending) != EnrollmentJournal::ReadStatus::Valid || !confirmed(pending))
    return Result::Held;
  char preparation[37], operation[37], nonce[EnrollmentRecovery::NonceHexLength + 1], request[200];
  uint8_t challenge[ChallengeSize], requestTag[TagSize], responseTag[TagSize], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!EnrollmentJournal::formatId(pending.preparation, preparation) ||
      !EnrollmentJournal::formatId(pending.operation, operation) ||
      !adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      !buildRequest(pending, nonce, request, sizeof(request), requestLength) ||
      !adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) ||
      !adapter.exchange(preparation, operation, pending.slot, nonce, challenge, requestTag,
                        response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response) ||
      !verifyResponse(pending, nonce,
          [&](const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
            return adapter.verify(challenge, body, length, tag, tagLength);
          }, response, responseLength, responseTag, sizeof(responseTag)))
    return Result::Held;

  EnrollmentJournal::Record current{};
  if (storage.read(current) != EnrollmentJournal::ReadStatus::Valid || !confirmed(current) ||
      current.slot != pending.slot ||
      memcmp(current.preparation, pending.preparation, EnrollmentJournal::IdSize) != 0 ||
      memcmp(current.operation, pending.operation, EnrollmentJournal::IdSize) != 0)
    return Result::Held;
  return storage.clear() ? Result::Completed : Result::Held;
}
}
