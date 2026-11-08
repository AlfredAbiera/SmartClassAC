#pragma once

#include "enrollment_journal.h"
#include "enrollment_recovery.h"

namespace ReplacementGuardStatus {
enum class State : uint8_t { Guarded, NotGuarded, Completed };
constexpr size_t ChallengeSize = 32;
constexpr size_t TagSize = 32;

inline bool buildRequest(const EnrollmentJournal::Record& pending, const char* nonce,
                         char* request, size_t capacity, size_t& length) {
  if (!EnrollmentJournal::valid(pending) || pending.kind != EnrollmentJournal::Kind::Replacement ||
      (pending.state != EnrollmentJournal::State::Uncertain &&
       pending.state != EnrollmentJournal::State::ReplacementPrepared) || !nonce ||
      strlen(nonce) != EnrollmentRecovery::NonceHexLength || !request) return false;
  for (size_t i = 0; i < EnrollmentRecovery::NonceHexLength; ++i)
    if (!((nonce[i] >= '0' && nonce[i] <= '9') || (nonce[i] >= 'a' && nonce[i] <= 'f'))) return false;
  char preparation[37], operation[37];
  if (!EnrollmentJournal::formatId(pending.preparation, preparation) ||
      !EnrollmentJournal::formatId(pending.operation, operation)) return false;
  return EnrollmentRecovery::buildRequestBody(preparation, operation, pending.slot, nonce, request, capacity, length);
}

template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& pending, const char* nonce, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength,
                    State& state) {
  char request[160];
  size_t requestLength = 0;
  if (!body || !tag || tagLength != TagSize || bodyLength > 256 ||
      !buildRequest(pending, nonce, request, sizeof(request), requestLength) ||
      bodyLength < requestLength || memcmp(body, request, requestLength) != 0) return false;
  State candidate;
  if (bodyLength == requestLength + sizeof("guarded\n") - 1 &&
      memcmp(body + requestLength, "guarded\n", sizeof("guarded\n") - 1) == 0)
    candidate = State::Guarded;
  else if (bodyLength == requestLength + sizeof("not_guarded\n") - 1 &&
           memcmp(body + requestLength, "not_guarded\n", sizeof("not_guarded\n") - 1) == 0)
    candidate = State::NotGuarded;
  else if (bodyLength == requestLength + sizeof("completed\n") - 1 &&
           memcmp(body + requestLength, "completed\n", sizeof("completed\n") - 1) == 0)
    candidate = State::Completed;
  else return false;
  if (!verify(body, bodyLength, tag, tagLength)) return false;
  state = candidate;
  return true;
}

// Every state is informational; a pending journal must remain held.
template <typename Adapter>
bool observe(const EnrollmentJournal::Record& pending, Adapter& adapter, State& state) {
  if (!EnrollmentJournal::valid(pending) || pending.kind != EnrollmentJournal::Kind::Replacement ||
      (pending.state != EnrollmentJournal::State::Uncertain &&
       pending.state != EnrollmentJournal::State::ReplacementPrepared)) return false;
  char preparation[37], operation[37], nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[160];
  uint8_t challenge[ChallengeSize], requestTag[TagSize], responseTag[TagSize], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!EnrollmentJournal::formatId(pending.preparation, preparation) ||
      !EnrollmentJournal::formatId(pending.operation, operation) ||
      !adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      nonce[EnrollmentRecovery::NonceHexLength] != '\0' ||
      !buildRequest(pending, nonce, request, sizeof(request), requestLength) ||
      !adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) ||
      !adapter.exchange(preparation, operation, pending.slot, nonce, challenge, requestTag,
                        response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response))
    return false;
  return verifyResponse(pending, nonce,
      [&](const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
        return adapter.verify(challenge, body, length, tag, tagLength);
      }, response, responseLength, responseTag, sizeof(responseTag), state);
}
}
