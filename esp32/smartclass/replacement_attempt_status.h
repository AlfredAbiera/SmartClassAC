#pragma once

#include "enrollment_journal.h"
#include "enrollment_recovery.h"

namespace ReplacementAttemptStatus {
enum class State : uint8_t { Reserved, NotReserved };
constexpr size_t ChallengeSize = 32;
constexpr size_t TagSize = 32;

inline bool buildRequest(const EnrollmentJournal::Record& pending, const char* nonce,
                         char* request, size_t capacity, size_t& length) {
  if (!EnrollmentJournal::valid(pending) || pending.kind != EnrollmentJournal::Kind::ReplacementAttempt ||
      (pending.state != EnrollmentJournal::State::Uncertain &&
       pending.state != EnrollmentJournal::State::RecoveryPrepared) ||
      !nonce || strlen(nonce) != EnrollmentRecovery::NonceHexLength || !request) return false;
  for (size_t i = 0; i < EnrollmentRecovery::NonceHexLength; ++i)
    if (!((nonce[i] >= '0' && nonce[i] <= '9') || (nonce[i] >= 'a' && nonce[i] <= 'f'))) return false;
  char preparation[37], operation[37], recovery[37];
  if (!EnrollmentJournal::formatId(pending.preparation, preparation) ||
      !EnrollmentJournal::formatId(pending.operation, operation) ||
      !EnrollmentJournal::formatId(pending.recoveryOperation, recovery)) return false;
  int written = snprintf(request, capacity, "%s\n%s\n%s\n%u\n%s\n",
                         preparation, operation, recovery, static_cast<unsigned>(pending.slot), nonce);
  if (written <= 0 || static_cast<size_t>(written) >= capacity) return false;
  length = static_cast<size_t>(written);
  return true;
}

template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& pending, const char* nonce, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength,
                    State& state) {
  char request[200];
  size_t requestLength = 0;
  if (!body || !tag || tagLength != TagSize || bodyLength > 256 ||
      !buildRequest(pending, nonce, request, sizeof(request), requestLength) ||
      bodyLength < requestLength || memcmp(body, request, requestLength) != 0) return false;
  State candidate;
  if (bodyLength == requestLength + sizeof("reserved\n") - 1 &&
      memcmp(body + requestLength, "reserved\n", sizeof("reserved\n") - 1) == 0)
    candidate = State::Reserved;
  else if (bodyLength == requestLength + sizeof("not_reserved\n") - 1 &&
           memcmp(body + requestLength, "not_reserved\n", sizeof("not_reserved\n") - 1) == 0)
    candidate = State::NotReserved;
  else return false;
  if (!verify(body, bodyLength, tag, tagLength)) return false;
  state = candidate;
  return true;
}

// An authenticated status is a snapshot, not permission to write or clear the journal.
template <typename Adapter>
bool observe(const EnrollmentJournal::Record& pending, Adapter& adapter, State& state) {
  if (!EnrollmentJournal::valid(pending) || pending.kind != EnrollmentJournal::Kind::ReplacementAttempt ||
      (pending.state != EnrollmentJournal::State::Uncertain &&
       pending.state != EnrollmentJournal::State::RecoveryPrepared))
    return false;
  char preparation[37], operation[37], recovery[37], nonce[EnrollmentRecovery::NonceHexLength + 1], request[200];
  uint8_t challenge[ChallengeSize], requestTag[TagSize], responseTag[TagSize], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!EnrollmentJournal::formatId(pending.preparation, preparation) ||
      !EnrollmentJournal::formatId(pending.operation, operation) ||
      !EnrollmentJournal::formatId(pending.recoveryOperation, recovery) ||
      !adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      !buildRequest(pending, nonce, request, sizeof(request), requestLength) ||
      !adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) ||
      !adapter.exchange(preparation, operation, recovery, pending.slot, nonce, challenge, requestTag,
                        response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response))
    return false;
  return verifyResponse(pending, nonce,
      [&](const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength) {
        return adapter.verify(challenge, body, bodyLength, tag, tagLength);
      }, response, responseLength, responseTag, sizeof(responseTag), state);
}
}
