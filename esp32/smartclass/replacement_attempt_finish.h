#pragma once

#include "replacement_attempt_write.h"
#include "enrollment_recovery.h"

namespace ReplacementAttemptFinish {
enum class Result : uint8_t { Completed, Held };

inline bool buildRequest(const EnrollmentJournal::Record& record, const char* nonce,
                         char* output, size_t capacity, size_t& length) {
  if (!EnrollmentJournal::valid(record) || record.kind != EnrollmentJournal::Kind::ReplacementAttempt ||
      record.state != EnrollmentJournal::State::Confirmed || !nonce ||
      strlen(nonce) != EnrollmentRecovery::NonceHexLength || !output) return false;
  for (size_t i = 0; i < EnrollmentRecovery::NonceHexLength; ++i)
    if (!((nonce[i] >= '0' && nonce[i] <= '9') || (nonce[i] >= 'a' && nonce[i] <= 'f'))) return false;
  char preparation[37], operation[37], recovery[37];
  if (!EnrollmentJournal::formatId(record.preparation, preparation) ||
      !EnrollmentJournal::formatId(record.operation, operation) ||
      !EnrollmentJournal::formatId(record.recoveryOperation, recovery)) return false;
  int written = snprintf(output, capacity, "%s\n%s\n%s\n%u\n%s\nconfirmed\n",
      preparation, operation, recovery, static_cast<unsigned>(record.slot), nonce);
  if (written <= 0 || static_cast<size_t>(written) >= capacity) return false;
  length = static_cast<size_t>(written);
  return true;
}

template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& pending, const char* nonce, Verify verify,
                    const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
  char request[240];
  size_t requestLength = 0;
  if (!body || !tag || tagLength != 32 ||
      !buildRequest(pending, nonce, request, sizeof(request), requestLength) ||
      length != requestLength + sizeof("completed\n") - 1 ||
      memcmp(body, request, requestLength) != 0 ||
      memcmp(body + requestLength, "completed\n", sizeof("completed\n") - 1) != 0)
    return false;
  return verify(body, length, tag, tagLength);
}

template <typename Storage, typename Adapter>
Result finish(Storage& storage, Adapter& adapter) {
  EnrollmentJournal::Record pending{};
  if (storage.read(pending) != EnrollmentJournal::ReadStatus::Valid ||
      !ReplacementAttemptWrite::sameAttempt(pending, pending, EnrollmentJournal::State::Confirmed))
    return Result::Held;
  char preparation[37], operation[37], recovery[37], nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[240];
  uint8_t challenge[32], requestTag[32], responseTag[32], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!EnrollmentJournal::formatId(pending.preparation, preparation) ||
      !EnrollmentJournal::formatId(pending.operation, operation) ||
      !EnrollmentJournal::formatId(pending.recoveryOperation, recovery) ||
      !adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      nonce[EnrollmentRecovery::NonceHexLength] != '\0' ||
      !buildRequest(pending, nonce, request, sizeof(request), requestLength) ||
      !adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) ||
      !adapter.exchange(preparation, operation, recovery, pending.slot, nonce, challenge, requestTag,
                        response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response) ||
      !verifyResponse(pending, nonce,
          [&](const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
            return adapter.verify(challenge, body, length, tag, tagLength);
          }, response, responseLength, responseTag, sizeof(responseTag)))
    return Result::Held;
  EnrollmentJournal::Record current{};
  if (storage.read(current) != EnrollmentJournal::ReadStatus::Valid ||
      !ReplacementAttemptWrite::sameAttempt(pending, current, EnrollmentJournal::State::Confirmed))
    return Result::Held;
  return storage.clear() ? Result::Completed : Result::Held;
}
}
