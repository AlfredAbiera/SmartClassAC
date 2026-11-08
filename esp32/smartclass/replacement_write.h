#pragma once

#include "replacement_guard_reserve.h"

namespace ReplacementWrite {
enum class Result : uint8_t { Refused, JournalFailure, SensorUncertain, Confirmed };

// grant.claimWrite must verify a separate, one-use write grant bound to this
// exact journal and must exclude recovery. A guard receipt or status is not a grant.
// The caller must capture a fresh model before calling this routine.
template <typename Storage, typename Sensor, typename Grant>
Result writeOnce(Storage& storage, Sensor& sensor, Grant& grant, const EnrollmentJournal::Record& prepared) {
  EnrollmentJournal::Record current{};
  if (!ReplacementGuardReserve::samePrepared(prepared, prepared) ||
      storage.read(current) != EnrollmentJournal::ReadStatus::Valid ||
      !ReplacementGuardReserve::samePrepared(prepared, current))
    return Result::JournalFailure;
  if (!grant.claimWrite(prepared)) return Result::Refused;

  if (storage.read(current) != EnrollmentJournal::ReadStatus::Valid ||
      !ReplacementGuardReserve::samePrepared(prepared, current))
    return Result::JournalFailure;
  current.state = EnrollmentJournal::State::Uncertain;
  if (!storage.write(current)) return Result::JournalFailure;
  EnrollmentJournal::Record verified{};
  if (storage.read(verified) != EnrollmentJournal::ReadStatus::Valid ||
      !EnrollmentJournal::valid(verified) ||
      verified.kind != EnrollmentJournal::Kind::Replacement ||
      verified.state != EnrollmentJournal::State::Uncertain ||
      verified.slot != current.slot ||
      memcmp(verified.preparation, current.preparation, EnrollmentJournal::IdSize) != 0 ||
      memcmp(verified.operation, current.operation, EnrollmentJournal::IdSize) != 0)
    return Result::JournalFailure;

  if (sensor.storeModel(current.slot) != 0) return Result::SensorUncertain;
  current.state = EnrollmentJournal::State::Confirmed;
  if (!storage.write(current)) return Result::JournalFailure;
  if (storage.read(verified) != EnrollmentJournal::ReadStatus::Valid ||
      !EnrollmentJournal::valid(verified) ||
      verified.kind != EnrollmentJournal::Kind::Replacement ||
      verified.state != EnrollmentJournal::State::Confirmed ||
      verified.slot != current.slot ||
      memcmp(verified.preparation, current.preparation, EnrollmentJournal::IdSize) != 0 ||
      memcmp(verified.operation, current.operation, EnrollmentJournal::IdSize) != 0)
    return Result::JournalFailure;
  return Result::Confirmed;
}
}
