#pragma once

#include "enrollment_journal.h"
#include "enrollment_recovery.h"
#include "replacement_operation.h"

namespace FingerprintDeletion {
enum class Result : uint8_t { Completed, Refused, Held, Uncertain, CompletionPending };

inline bool same(const EnrollmentJournal::Record& a, const EnrollmentJournal::Record& b) {
  return EnrollmentJournal::valid(a) && EnrollmentJournal::valid(b) &&
      a.kind == EnrollmentJournal::Kind::Deletion && b.kind == EnrollmentJournal::Kind::Deletion &&
      a.slot == b.slot && memcmp(a.preparation, b.preparation, EnrollmentJournal::IdSize) == 0 &&
      memcmp(a.operation, b.operation, EnrollmentJournal::IdSize) == 0;
}

template <typename Adapter>
bool discover(Adapter& adapter, EnrollmentJournal::Record& operation) {
  char nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[EnrollmentRecovery::NonceHexLength + 2];
  uint8_t challenge[32], requestTag[32], responseTag[32], response[256];
  size_t length = 0;
  if (!adapter.getChallenge("operation", challenge) || !adapter.makeNonce(nonce) ||
      strlen(nonce) != EnrollmentRecovery::NonceHexLength) return false;
  memcpy(request, nonce, EnrollmentRecovery::NonceHexLength);
  request[EnrollmentRecovery::NonceHexLength] = '\n';
  request[EnrollmentRecovery::NonceHexLength + 1] = '\0';
  if (!adapter.sign("operation", challenge, reinterpret_cast<const uint8_t*>(request), sizeof(request) - 1, requestTag) ||
      !adapter.exchange("operation", nullptr, 0, nonce, challenge, requestTag, response, sizeof(response), length, responseTag) ||
      length > sizeof(response) || length < 37 + 1 + 1 + EnrollmentRecovery::NonceHexLength + sizeof("\ndelete\n") - 1 ||
      response[36] != '\n') return false;
  size_t slotStart = 37, slotEnd = slotStart;
  while (slotEnd < length && response[slotEnd] >= '0' && response[slotEnd] <= '9') slotEnd++;
  size_t digits = slotEnd - slotStart;
  if (digits == 0 || digits > 5 || response[slotStart] == '0' ||
      length != slotEnd + 1 + EnrollmentRecovery::NonceHexLength + sizeof("\ndelete\n") - 1 ||
      response[slotEnd] != '\n' ||
      memcmp(response + slotEnd + 1, nonce, EnrollmentRecovery::NonceHexLength) != 0 ||
      memcmp(response + slotEnd + 1 + EnrollmentRecovery::NonceHexLength, "\ndelete\n", sizeof("\ndelete\n") - 1) != 0 ||
      !adapter.verify("operation", challenge, response, length, responseTag)) return false;
  uint32_t slot = 0;
  for (size_t i = slotStart; i < slotEnd; ++i) slot = slot * 10 + response[i] - '0';
  if (slot == 0 || slot > UINT16_MAX) return false;
  EnrollmentJournal::Record result{};
  if (!ReplacementOperation::parseId(response, result.preparation)) return false;
  memcpy(result.operation, result.preparation, EnrollmentJournal::IdSize);
  result.slot = static_cast<uint16_t>(slot);
  result.kind = EnrollmentJournal::Kind::Deletion;
  result.state = EnrollmentJournal::State::Uncertain;
  if (!EnrollmentJournal::valid(result)) return false;
  operation = result;
  return true;
}

template <typename Adapter>
bool exchange(Adapter& adapter, const char* action, const EnrollmentJournal::Record& operation, const char* state, const char* reply) {
  if (!EnrollmentJournal::valid(operation) || operation.kind != EnrollmentJournal::Kind::Deletion) return false;
  char id[37], nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[160], expected[180];
  uint8_t challenge[32], requestTag[32], responseTag[32], response[256];
  size_t length = 0;
  if (!EnrollmentJournal::formatId(operation.preparation, id) || !adapter.getChallenge(action, challenge) ||
      !adapter.makeNonce(nonce) || strlen(nonce) != EnrollmentRecovery::NonceHexLength) return false;
  int requestLength = snprintf(request, sizeof(request), "%s\n%u\n%s\n%s\n", id,
      static_cast<unsigned>(operation.slot), nonce, state);
  if (requestLength <= 0 || static_cast<size_t>(requestLength) >= sizeof(request)) return false;
  int expectedLength = snprintf(expected, sizeof(expected), "%s%s\n", request, reply);
  if (expectedLength <= 0 || static_cast<size_t>(expectedLength) >= sizeof(expected)) return false;
  return adapter.sign(action, challenge, reinterpret_cast<const uint8_t*>(request), requestLength, requestTag) &&
      adapter.exchange(action, id, operation.slot, nonce, challenge, requestTag, response, sizeof(response), length, responseTag) &&
      length == static_cast<size_t>(expectedLength) && memcmp(response, expected, length) == 0 &&
      adapter.verify(action, challenge, response, length, responseTag);
}

template <typename Storage, typename Adapter>
Result finish(Storage& storage, Adapter& adapter) {
  EnrollmentJournal::Record pending{}, current{};
  if (storage.read(pending) != EnrollmentJournal::ReadStatus::Valid ||
      !EnrollmentJournal::valid(pending) || pending.kind != EnrollmentJournal::Kind::Deletion ||
      pending.state != EnrollmentJournal::State::Confirmed) return Result::Held;
  if (!exchange(adapter, "finish", pending, "confirmed", "completed")) return Result::CompletionPending;
  if (storage.read(current) != EnrollmentJournal::ReadStatus::Valid || !same(pending, current) ||
      current.state != EnrollmentJournal::State::Confirmed) return Result::Held;
  return storage.clear() ? Result::Completed : Result::Held;
}

// A durable uncertain journal precedes the one-use server claim. An uncertain
// result never retries the sensor command; the slot remains blocked.
template <typename Storage, typename Sensor, typename Adapter>
Result runOnce(Storage& storage, Sensor& sensor, Adapter& adapter, const EnrollmentJournal::Record& operation) {
  EnrollmentJournal::Record current{}, verified{};
  bool occupied = false;
  if (!EnrollmentJournal::valid(operation) || operation.kind != EnrollmentJournal::Kind::Deletion ||
      operation.state != EnrollmentJournal::State::Uncertain ||
      storage.read(current) != EnrollmentJournal::ReadStatus::Empty) return Result::Held;
  if (sensor.inspectSlot(operation.slot, occupied) != 0 || !occupied) return Result::Refused;
  if (!storage.write(operation) || storage.read(verified) != EnrollmentJournal::ReadStatus::Valid ||
      !same(operation, verified) || verified.state != EnrollmentJournal::State::Uncertain) return Result::Held;
  if (!exchange(adapter, "claim", operation, "claim", "granted")) return Result::Held;
  if (storage.read(verified) != EnrollmentJournal::ReadStatus::Valid || !same(operation, verified) ||
      verified.state != EnrollmentJournal::State::Uncertain) return Result::Held;
  if (sensor.deleteModel(operation.slot) != 0 ||
      sensor.inspectSlot(operation.slot, occupied) != 0 || occupied) return Result::Uncertain;
  verified.state = EnrollmentJournal::State::Confirmed;
  if (!storage.write(verified) || storage.read(current) != EnrollmentJournal::ReadStatus::Valid ||
      !same(operation, current) || current.state != EnrollmentJournal::State::Confirmed) return Result::Held;
  return finish(storage, adapter);
}
}
