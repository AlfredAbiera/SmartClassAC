#pragma once

#include "replacement_attempt_status.h"

namespace ReplacementAttemptClaim {
inline bool prepared(const EnrollmentJournal::Record& attempt) {
  return EnrollmentJournal::valid(attempt) && attempt.kind == EnrollmentJournal::Kind::ReplacementAttempt &&
      attempt.state == EnrollmentJournal::State::RecoveryPrepared;
}

template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& attempt, const char* nonce, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength) {
  char request[200];
  size_t requestLength = 0;
  if (!prepared(attempt) || !body || !tag || tagLength != ReplacementAttemptStatus::TagSize ||
      !ReplacementAttemptStatus::buildRequest(attempt, nonce, request, sizeof(request), requestLength) ||
      bodyLength != requestLength + sizeof("granted\n") - 1 ||
      memcmp(body, request, requestLength) != 0 ||
      memcmp(body + requestLength, "granted\n", sizeof("granted\n") - 1) != 0)
    return false;
  return verify(body, bodyLength, tag, tagLength);
}

template <typename Adapter>
bool claim(const EnrollmentJournal::Record& attempt, Adapter& adapter) {
  if (!prepared(attempt)) return false;
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
      responseLength > sizeof(response))
    return false;
  return verifyResponse(attempt, nonce,
      [&](const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
        return adapter.verify(challenge, body, length, tag, tagLength);
      }, response, responseLength, responseTag, sizeof(responseTag));
}
}
