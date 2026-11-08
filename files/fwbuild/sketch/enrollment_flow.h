#line 1 "D:\\SmartClassAC-ESP32\\smartclass\\enrollment_flow.h"
#pragma once

#include <stdint.h>
#include "enrollment_journal.h"
#include "enrollment_recovery.h"

// Pure decisions for the enrollment write path. Nothing here touches the
// sensor or storage; callers supply the observed facts and act on the result.
namespace EnrollmentFlow {

enum class WriteDecision : uint8_t {
  Proceed,              // journal empty, slot empty, server authorized, no completion on record
  RefuseJournalPending, // an earlier write is unresolved; never write again on top of it
  RefuseSlotOccupied,   // target slot holds a template or its state is unknown
  RefuseNotAuthorized,  // server has not issued write authorization for this operation
  RefuseServerCompleted,// server already records this operation as completed
  RefuseSensorError,    // index read failed or sensor is poisoned
  RefuseJournalError    // journal could not be read safely
};

enum class ResumeDecision : uint8_t {
  Nothing,         // no pending record
  MarkConfirmed,   // server confirms completion and the slot is occupied: safe to confirm locally
  HoldForOperator  // cannot prove the outcome; keep the record and do not write
};

struct WriteFacts {
  EnrollmentJournal::ReadStatus journal;
  bool sensorOk;            // inspectSlot returned Ok
  bool slotOccupied;        // valid only when sensorOk
  bool serverAuthorized;    // server issued write authorization for this operation
  bool haveServerState;     // recovery state was obtained and verified
  EnrollmentRecovery::State serverState;  // valid only when haveServerState
};

inline WriteDecision decideWrite(const WriteFacts& facts) {
  if (facts.journal == EnrollmentJournal::ReadStatus::Error) return WriteDecision::RefuseJournalError;
  if (facts.journal == EnrollmentJournal::ReadStatus::Valid) return WriteDecision::RefuseJournalPending;
  if (!facts.sensorOk) return WriteDecision::RefuseSensorError;
  if (facts.slotOccupied) return WriteDecision::RefuseSlotOccupied;
  if (facts.haveServerState && facts.serverState == EnrollmentRecovery::State::Completed)
    return WriteDecision::RefuseServerCompleted;
  if (!facts.serverAuthorized) return WriteDecision::RefuseNotAuthorized;
  return WriteDecision::Proceed;
}

struct ResumeFacts {
  EnrollmentJournal::ReadStatus journal;
  EnrollmentJournal::State journalState;  // valid only when journal is Valid
  bool sensorOk;
  bool slotOccupied;
  bool haveServerState;
  EnrollmentRecovery::State serverState;
};

// Decides what to do with a record left by an earlier run. An Uncertain
// record is confirmed only when the server reports completion and the sensor
// shows the slot occupied. Otherwise it is held. This never returns a write.
inline ResumeDecision decideResume(const ResumeFacts& facts) {
  if (facts.journal == EnrollmentJournal::ReadStatus::Empty) return ResumeDecision::Nothing;
  if (facts.journal != EnrollmentJournal::ReadStatus::Valid) return ResumeDecision::HoldForOperator;
  if (facts.journalState == EnrollmentJournal::State::Confirmed) return ResumeDecision::Nothing;
  if (facts.sensorOk && facts.slotOccupied && facts.haveServerState &&
      facts.serverState == EnrollmentRecovery::State::Completed)
    return ResumeDecision::MarkConfirmed;
  return ResumeDecision::HoldForOperator;
}
}
