#pragma once

#include "replacement_attempt_write.h"
#include "replacement_attempt_status.h"

namespace ReplacementAttemptFence {
// The caller must serialize this operation with all sensor writes and discard
// any in-memory grant. This local fence alone does not permit a new attempt.
template <typename Storage>
bool fence(Storage& storage, const EnrollmentJournal::Record& expected) {
  using namespace EnrollmentJournal;
  if (!valid(expected) || expected.kind != Kind::ReplacementAttempt ||
      (expected.state != State::RecoveryPrepared && expected.state != State::Uncertain &&
       expected.state != State::RecoveryFenced)) return false;
  Record current{};
  if (storage.read(current) != ReadStatus::Valid ||
      !ReplacementAttemptWrite::sameAttempt(expected, current, expected.state)) return false;
  if (current.state != State::RecoveryFenced) {
    current.state = State::RecoveryFenced;
    if (!storage.write(current)) return false;
  }
  Record verified{};
  return storage.read(verified) == ReadStatus::Valid &&
      ReplacementAttemptWrite::sameAttempt(current, verified, State::RecoveryFenced);
}

inline bool buildRequest(const EnrollmentJournal::Record& fenced, const char* nonce,
                         char* output, size_t capacity, size_t& length) {
  if (!EnrollmentJournal::valid(fenced) || fenced.kind != EnrollmentJournal::Kind::ReplacementAttempt ||
      fenced.state != EnrollmentJournal::State::RecoveryFenced) return false;
  auto prepared = fenced;
  prepared.state = EnrollmentJournal::State::RecoveryPrepared;
  size_t prefixLength = 0;
  if (!ReplacementAttemptStatus::buildRequest(prepared, nonce, output, capacity, prefixLength) ||
      capacity - prefixLength <= sizeof("fenced\n") - 1) return false;
  memcpy(output + prefixLength, "fenced\n", sizeof("fenced\n"));
  length = prefixLength + sizeof("fenced\n") - 1;
  return true;
}

template <typename Verify>
bool verifyResponse(const EnrollmentJournal::Record& fenced, const char* nonce, Verify verify,
                    const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
  char request[240];
  size_t requestLength = 0;
  if (!body || !tag || tagLength != 32 ||
      !buildRequest(fenced, nonce, request, sizeof(request), requestLength) ||
      length != requestLength + sizeof("fenced_only\n") - 1 ||
      memcmp(body, request, requestLength) != 0 ||
      memcmp(body + requestLength, "fenced_only\n", sizeof("fenced_only\n") - 1) != 0)
    return false;
  return verify(body, length, tag, tagLength);
}

enum class Result : uint8_t { Held, FencedHeld };

// Local fencing and abandoning grants must precede the authenticated attestation.
template <typename Storage, typename Adapter>
Result attest(Storage& storage, Adapter& adapter, const EnrollmentJournal::Record& expected) {
  using namespace EnrollmentJournal;
  if (!fence(storage, expected)) return Result::Held;
  Record pending{};
  if (storage.read(pending) != ReadStatus::Valid || pending.state != State::RecoveryFenced ||
      !valid(pending) || pending.slot != expected.slot ||
      memcmp(pending.preparation, expected.preparation, IdSize) != 0 ||
      memcmp(pending.operation, expected.operation, IdSize) != 0 ||
      memcmp(pending.recoveryOperation, expected.recoveryOperation, IdSize) != 0) return Result::Held;
  char preparation[37], operation[37], recovery[37], nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[240];
  uint8_t challenge[32], requestTag[32], responseTag[32], response[256];
  size_t requestLength = 0, responseLength = 0;
  if (!formatId(pending.preparation, preparation) || !formatId(pending.operation, operation) ||
      !formatId(pending.recoveryOperation, recovery) || !adapter.getChallenge(challenge) ||
      !adapter.makeNonce(nonce) || nonce[EnrollmentRecovery::NonceHexLength] != '\0' ||
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
  Record current{};
  if (storage.read(current) != ReadStatus::Valid ||
      !ReplacementAttemptWrite::sameAttempt(pending, current, State::RecoveryFenced)) return Result::Held;
  return Result::FencedHeld;
}
}
