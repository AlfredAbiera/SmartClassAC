#pragma once

#include <stdint.h>
#include "enrollment_flow.h"
#include "enrollment_journal.h"
#include "enrollment_recovery.h"
#include "enrollment_sensor.h"

// One pass of the enrollment write path. Every sensor and network effect goes
// through the supplied adapters, so the same logic runs on the board and in tests.
namespace EnrollmentStep {

enum class Outcome : uint8_t {
  Idle,           // nothing pending, nothing authorized, nothing written
  Written,        // journal confirmed after a successful store
  Resumed,        // earlier record confirmed from authenticated server completion
  Held,           // earlier record left for an operator; no write attempted
  Refused,        // write decision refused; see decision
  CaptureFailure, // model capture failed before authorization or journal write
  CompletionPending, // sensor result is confirmed but server completion or journal cleanup is pending
  Uncertain,      // store result unknown; record stays uncertain, never rewritten
  JournalFailure  // storage failed; no sensor write was attempted
};

struct Result {
  Outcome outcome;
  EnrollmentFlow::WriteDecision decision;
};

// The server adapter supplies fetchRecoveryState (false when no verified answer
// was obtained) and claimWrite (true only for a verified grant). Neither answer
// is written to the sensor by itself.
template <typename Storage, typename Sensor, typename Server>
Result runOnce(Storage& storage, Sensor& sensor, Server& server, const EnrollmentJournal::Record& intended) {
  EnrollmentJournal::Record pending{};
  EnrollmentJournal::ReadStatus journal = storage.read(pending);
  if (journal == EnrollmentJournal::ReadStatus::Error)
    return {Outcome::JournalFailure, EnrollmentFlow::WriteDecision::RefuseJournalError};

  if (journal == EnrollmentJournal::ReadStatus::Valid) {
    if (pending.kind != EnrollmentJournal::Kind::NewEnrollment)
      return {Outcome::Held, EnrollmentFlow::WriteDecision::RefuseJournalPending};
    bool occupied = false;
    uint8_t sensorResult = sensor.inspectSlot(pending.slot, occupied);
    bool sensorOk = sensorResult == EnrollmentSensor::Ok;
    EnrollmentRecovery::State serverState = EnrollmentRecovery::State::Unresolved;
    bool haveServerState = server.fetchRecoveryState(pending, serverState);
    if (!sensorOk || !occupied) return {Outcome::Held, EnrollmentFlow::WriteDecision::RefuseJournalPending};

    if (haveServerState && serverState == EnrollmentRecovery::State::Completed) {
      if (pending.state != EnrollmentJournal::State::Confirmed) {
        pending.state = EnrollmentJournal::State::Confirmed;
        if (!storage.write(pending))
          return {Outcome::JournalFailure, EnrollmentFlow::WriteDecision::RefuseJournalError};
      }
      return EnrollmentJournal::clearConfirmed(storage)
        ? Result{Outcome::Resumed, EnrollmentFlow::WriteDecision::RefuseJournalPending}
        : Result{Outcome::CompletionPending, EnrollmentFlow::WriteDecision::RefuseJournalPending};
    }

    if (pending.state != EnrollmentJournal::State::Confirmed)
      return {Outcome::Held, EnrollmentFlow::WriteDecision::RefuseJournalPending};
    if (!server.finishWrite(pending.preparation, pending.operation, pending.slot))
      return {Outcome::CompletionPending, EnrollmentFlow::WriteDecision::RefuseJournalPending};
    return EnrollmentJournal::clearConfirmed(storage)
      ? Result{Outcome::Written, EnrollmentFlow::WriteDecision::RefuseJournalPending}
      : Result{Outcome::CompletionPending, EnrollmentFlow::WriteDecision::RefuseJournalPending};
  }

  // An empty journal has no prior outcome to recover. The operation endpoint
  // supplies only active consent, and claim revalidates it transactionally.
  if (intended.kind != EnrollmentJournal::Kind::NewEnrollment)
    return {Outcome::Refused, EnrollmentFlow::WriteDecision::RefuseNotAuthorized};
  bool occupied = false;
  uint8_t sensorResult = sensor.inspectSlot(intended.slot, occupied);
  bool sensorOk = sensorResult == EnrollmentSensor::Ok;

  // A claim creates a server write guard, so it is requested only when every
  // local precondition already holds. Its answer then gates the write itself.
  EnrollmentFlow::WriteFacts facts{
    journal, sensorOk, occupied, true, false, EnrollmentRecovery::State::Unresolved
  };
  EnrollmentFlow::WriteDecision decision = EnrollmentFlow::decideWrite(facts);
  if (decision != EnrollmentFlow::WriteDecision::Proceed) {
    if (decision == EnrollmentFlow::WriteDecision::RefuseJournalError) return {Outcome::JournalFailure, decision};
    return {Outcome::Refused, decision};
  }
  if (!sensor.prepareModel()) return {Outcome::CaptureFailure, decision};
  if (!server.claimWrite(intended.preparation, intended.operation, intended.slot)) {
    return {Outcome::Refused, EnrollmentFlow::WriteDecision::RefuseNotAuthorized};
  }

  switch (EnrollmentJournal::writeOnce(storage, sensor, intended)) {
    case EnrollmentJournal::WriteResult::Confirmed:
      if (!server.finishWrite(intended.preparation, intended.operation, intended.slot))
        return {Outcome::CompletionPending, decision};
      return EnrollmentJournal::clearConfirmed(storage)
        ? Result{Outcome::Written, decision}
        : Result{Outcome::CompletionPending, decision};
    case EnrollmentJournal::WriteResult::SensorUncertain:
      return {Outcome::Uncertain, decision};
    case EnrollmentJournal::WriteResult::AlreadyPending:
      return {Outcome::Held, EnrollmentFlow::WriteDecision::RefuseJournalPending};
    case EnrollmentJournal::WriteResult::JournalError:
      return {Outcome::JournalFailure, decision};
  }
  return {Outcome::JournalFailure, decision};
}
}
