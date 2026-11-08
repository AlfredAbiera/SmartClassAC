#pragma once

#include "replacement_attempt_status.h"
#include "replacement_write_fence.h"

namespace ReplacementAttemptReserve {
enum class Result : uint8_t { Held, ReservedHeld };

template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& attempt, const char* nonce, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength) {
  char request[200];
  size_t requestLength = 0;
  if (!body || !tag || tagLength != ReplacementAttemptStatus::TagSize ||
      attempt.state != EnrollmentJournal::State::RecoveryPrepared ||
      !ReplacementAttemptStatus::buildRequest(attempt, nonce, request, sizeof(request), requestLength) ||
      bodyLength != requestLength + sizeof("reserved_only\n") - 1 ||
      memcmp(body, request, requestLength) != 0 ||
      memcmp(body + requestLength, "reserved_only\n", sizeof("reserved_only\n") - 1) != 0)
    return false;
  return verify(body, bodyLength, tag, tagLength);
}

// recoveryId must come from the verified intent for the exact fenced journal.
// Caller must serialize this routine with all sensor writes.
template <typename Storage, typename Adapter>
Result reserve(Storage& storage, Adapter& adapter, const EnrollmentJournal::Record& fenced,
               const uint8_t* recoveryId) {
  if (!ReplacementWriteFence::fenced(fenced) ||
      !EnrollmentJournal::recordRecoveryAttempt(storage, fenced, recoveryId))
    return Result::Held;
  EnrollmentJournal::Record attempt{};
  if (storage.read(attempt) != EnrollmentJournal::ReadStatus::Valid || !EnrollmentJournal::valid(attempt) ||
      attempt.kind != EnrollmentJournal::Kind::ReplacementAttempt ||
      attempt.state != EnrollmentJournal::State::RecoveryPrepared ||
      attempt.slot != fenced.slot ||
      memcmp(attempt.preparation, fenced.preparation, EnrollmentJournal::IdSize) != 0 ||
      memcmp(attempt.operation, fenced.operation, EnrollmentJournal::IdSize) != 0 ||
      memcmp(attempt.recoveryOperation, recoveryId, EnrollmentJournal::IdSize) != 0)
    return Result::Held;
  char preparation[37], operation[37], recovery[37], nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[200];
  uint8_t challenge[32], requestTag[32], responseTag[32], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!EnrollmentJournal::formatId(attempt.preparation, preparation) ||
      !EnrollmentJournal::formatId(attempt.operation, operation) ||
      !EnrollmentJournal::formatId(attempt.recoveryOperation, recovery) ||
      !adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      nonce[EnrollmentRecovery::NonceHexLength] != '\0' ||
      !ReplacementAttemptStatus::buildRequest(attempt, nonce, request, sizeof(request), requestLength) ||
      !adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) ||
      !adapter.exchange(preparation, operation, recovery, attempt.slot, nonce, challenge, requestTag,
                        response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response) ||
      !verifyResponse(attempt, nonce,
          [&](const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
            return adapter.verify(challenge, body, length, tag, tagLength);
          }, response, responseLength, responseTag, sizeof(responseTag)))
    return Result::Held;
  EnrollmentJournal::Record current{};
  if (storage.read(current) != EnrollmentJournal::ReadStatus::Valid || !EnrollmentJournal::valid(current) ||
      current.kind != EnrollmentJournal::Kind::ReplacementAttempt ||
      current.state != EnrollmentJournal::State::RecoveryPrepared || current.slot != attempt.slot ||
      memcmp(current.preparation, attempt.preparation, EnrollmentJournal::IdSize) != 0 ||
      memcmp(current.operation, attempt.operation, EnrollmentJournal::IdSize) != 0 ||
      memcmp(current.recoveryOperation, attempt.recoveryOperation, EnrollmentJournal::IdSize) != 0)
    return Result::Held;
  return Result::ReservedHeld;
}
}
