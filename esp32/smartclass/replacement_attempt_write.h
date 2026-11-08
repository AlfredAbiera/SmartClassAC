#pragma once

#include "replacement_attempt_claim.h"

namespace ReplacementAttemptWrite {
enum class Result : uint8_t { Refused, JournalFailure, SensorUncertain, Confirmed };

inline bool sameAttempt(const EnrollmentJournal::Record& a, const EnrollmentJournal::Record& b,
                        EnrollmentJournal::State state) {
  return EnrollmentJournal::valid(a) && EnrollmentJournal::valid(b) &&
      a.kind == EnrollmentJournal::Kind::ReplacementAttempt &&
      b.kind == EnrollmentJournal::Kind::ReplacementAttempt &&
      a.state == state && b.state == state && a.slot == b.slot &&
      memcmp(a.preparation, b.preparation, EnrollmentJournal::IdSize) == 0 &&
      memcmp(a.operation, b.operation, EnrollmentJournal::IdSize) == 0 &&
      memcmp(a.recoveryOperation, b.recoveryOperation, EnrollmentJournal::IdSize) == 0;
}

// The caller must capture a fresh model and serialize all writes. The grant
// must be verified for this exact attempt; a reservation receipt is not a grant.
template <typename Storage, typename Sensor, typename Grant>
Result writeOnce(Storage& storage, Sensor& sensor, Grant& grant, const EnrollmentJournal::Record& prepared) {
  EnrollmentJournal::Record current{};
  if (!ReplacementAttemptClaim::prepared(prepared) ||
      storage.read(current) != EnrollmentJournal::ReadStatus::Valid ||
      !sameAttempt(prepared, current, EnrollmentJournal::State::RecoveryPrepared))
    return Result::JournalFailure;
  if (!grant.claimWrite(prepared)) return Result::Refused;
  if (storage.read(current) != EnrollmentJournal::ReadStatus::Valid ||
      !sameAttempt(prepared, current, EnrollmentJournal::State::RecoveryPrepared))
    return Result::JournalFailure;
  current.state = EnrollmentJournal::State::Uncertain;
  if (!storage.write(current)) return Result::JournalFailure;
  EnrollmentJournal::Record verified{};
  if (storage.read(verified) != EnrollmentJournal::ReadStatus::Valid ||
      !sameAttempt(current, verified, EnrollmentJournal::State::Uncertain))
    return Result::JournalFailure;
  if (sensor.storeModel(current.slot) != 0) return Result::SensorUncertain;
  current.state = EnrollmentJournal::State::Confirmed;
  if (!storage.write(current)) return Result::JournalFailure;
  if (storage.read(verified) != EnrollmentJournal::ReadStatus::Valid ||
      !sameAttempt(current, verified, EnrollmentJournal::State::Confirmed))
    return Result::JournalFailure;
  return Result::Confirmed;
}
}
