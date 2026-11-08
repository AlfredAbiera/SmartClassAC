#pragma once

#include "enrollment_step.h"

namespace EnrollmentNotice {
struct Message {
  const char* line0;
  const char* line1;
};

inline Message forResult(const EnrollmentStep::Result& result) {
  switch (result.outcome) {
    case EnrollmentStep::Outcome::Written: return {"Enroll complete", "Remove finger"};
    case EnrollmentStep::Outcome::Resumed: return {"Recovery done", "Remove finger"};
    case EnrollmentStep::Outcome::CaptureFailure: return {"Capture failed", "Retry with admin"};
    case EnrollmentStep::Outcome::CompletionPending: return {"Finish pending", "Contact admin"};
    case EnrollmentStep::Outcome::Uncertain: return {"Write uncertain", "Contact admin"};
    case EnrollmentStep::Outcome::Held: return {"Recovery held", "Contact admin"};
    case EnrollmentStep::Outcome::JournalFailure: return {"Journal error", "Contact admin"};
    case EnrollmentStep::Outcome::Refused:
      if (result.decision == EnrollmentFlow::WriteDecision::RefuseSlotOccupied)
        return {"Slot occupied", "No write made"};
      if (result.decision == EnrollmentFlow::WriteDecision::RefuseNotAuthorized)
        return {"Not authorized", "Check consent"};
      return {"Enroll refused", "Check sensor"};
    case EnrollmentStep::Outcome::Idle: return {"No enrollment", "Check consent"};
  }
  return {"Enroll error", "Contact admin"};
}
}
