#pragma once

#include "enrollment_journal.h"
#include "enrollment_recovery.h"

namespace ReplacementIntent {

constexpr size_t ChallengeSize = 32;
constexpr size_t TagSize = 32;

// This only verifies an observation, not consent to write or recover.
template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& pending, const char* nonce, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength,
                    char (&recoveryOperation)[37]) {
  if (!EnrollmentJournal::valid(pending) || pending.kind != EnrollmentJournal::Kind::Replacement ||
      !nonce || strlen(nonce) != EnrollmentRecovery::NonceHexLength ||
      !body || !tag || tagLength != 32 || bodyLength > 256)
    return false;
  for (size_t i = 0; i < EnrollmentRecovery::NonceHexLength; ++i)
    if (!((nonce[i] >= '0' && nonce[i] <= '9') || (nonce[i] >= 'a' && nonce[i] <= 'f'))) return false;

  char preparation[37], operation[37], prefix[200];
  if (!EnrollmentJournal::formatId(pending.preparation, preparation) ||
      !EnrollmentJournal::formatId(pending.operation, operation)) return false;
  int prefixLength = snprintf(prefix, sizeof(prefix), "%s\n%s\n%u\n%s\n", preparation, operation,
                              static_cast<unsigned>(pending.slot), nonce);
  if (prefixLength <= 0 || static_cast<size_t>(prefixLength) >= sizeof(prefix)) return false;
  const size_t prefixSize = static_cast<size_t>(prefixLength);
  const char suffix[] = "\nintent_only\n";
  if (bodyLength != prefixSize + 36 + sizeof(suffix) - 1 ||
      memcmp(body, prefix, prefixSize) != 0 ||
      memcmp(body + prefixSize + 36, suffix, sizeof(suffix) - 1) != 0) return false;

  char candidate[37];
  bool nonzero = false;
  for (size_t i = 0; i < 36; ++i) {
    const char ch = static_cast<char>(body[prefixSize + i]);
    if (i == 8 || i == 13 || i == 18 || i == 23) {
      if (ch != '-') return false;
    } else {
      if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
      if (ch != '0') nonzero = true;
    }
    candidate[i] = ch;
  }
  if (!nonzero || !verify(body, bodyLength, tag, tagLength)) return false;
  candidate[36] = '\0';
  memcpy(recoveryOperation, candidate, sizeof(candidate));
  return true;
}

// Adapter methods only exchange and authenticate bytes. The result is never a
// write grant; the caller must keep the original journal and guard in place.
template <typename Adapter>
bool observe(const EnrollmentJournal::Record& pending, Adapter& adapter, char (&recoveryOperation)[37]) {
  if (!EnrollmentJournal::valid(pending) || pending.kind != EnrollmentJournal::Kind::Replacement)
    return false;
  char preparation[37], operation[37], nonce[EnrollmentRecovery::NonceHexLength + 1], request[160];
  uint8_t challenge[ChallengeSize], requestTag[TagSize], responseTag[TagSize], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!EnrollmentJournal::formatId(pending.preparation, preparation) ||
      !EnrollmentJournal::formatId(pending.operation, operation) ||
      !adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      !EnrollmentRecovery::buildRequestBody(preparation, operation, pending.slot, nonce, request, sizeof(request), requestLength) ||
      !adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) ||
      !adapter.exchange(preparation, operation, pending.slot, nonce, challenge, requestTag,
                        response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response))
    return false;
  return verifyResponse(pending, nonce,
      [&](const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength) {
        return adapter.verify(challenge, body, bodyLength, tag, tagLength);
      }, response, responseLength, responseTag, sizeof(responseTag), recoveryOperation);
}
}
