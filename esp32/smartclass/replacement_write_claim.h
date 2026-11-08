#pragma once

#include "replacement_guard_reserve.h"

namespace ReplacementWriteClaim {
template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& prepared, const char* nonce, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength) {
  char request[160];
  size_t requestLength = 0;
  if (!ReplacementGuardReserve::samePrepared(prepared, prepared) || !body || !tag ||
      tagLength != ReplacementGuardStatus::TagSize ||
      !ReplacementGuardStatus::buildRequest(prepared, nonce, request, sizeof(request), requestLength) ||
      bodyLength != requestLength + sizeof("granted\n") - 1 ||
      memcmp(body, request, requestLength) != 0 ||
      memcmp(body + requestLength, "granted\n", sizeof("granted\n") - 1) != 0)
    return false;
  return verify(body, bodyLength, tag, tagLength);
}

template <typename Adapter>
bool claim(const EnrollmentJournal::Record& prepared, Adapter& adapter) {
  if (!ReplacementGuardReserve::samePrepared(prepared, prepared)) return false;
  char preparation[37], operation[37], nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[160];
  uint8_t challenge[ReplacementGuardStatus::ChallengeSize], requestTag[ReplacementGuardStatus::TagSize];
  uint8_t responseTag[ReplacementGuardStatus::TagSize], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!EnrollmentJournal::formatId(prepared.preparation, preparation) ||
      !EnrollmentJournal::formatId(prepared.operation, operation) ||
      !adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      nonce[EnrollmentRecovery::NonceHexLength] != '\0' ||
      !ReplacementGuardStatus::buildRequest(prepared, nonce, request, sizeof(request), requestLength) ||
      !adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) ||
      !adapter.exchange(preparation, operation, prepared.slot, nonce, challenge, requestTag,
                        response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response))
    return false;
  return verifyResponse(prepared, nonce,
      [&](const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
        return adapter.verify(challenge, body, length, tag, tagLength);
      }, response, responseLength, responseTag, sizeof(responseTag));
}
}
