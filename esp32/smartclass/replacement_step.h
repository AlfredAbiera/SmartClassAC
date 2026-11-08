#pragma once

#include "replacement_write.h"
#include "replacement_finish.h"
#include "enrollment_sensor.h"

namespace ReplacementStep {
enum class Outcome : uint8_t {
  Completed, Refused, CaptureFailure, Held, Uncertain, CompletionPending
};

// The caller must exclusively own the sensor and serialize all enrollment writes.
template <typename Storage, typename Sensor, typename Reserve, typename Grant, typename Finish>
Outcome runOnce(Storage& storage, Sensor& sensor, Reserve& reserve, Grant& grant, Finish& finish,
                const EnrollmentJournal::Record& operation) {
  EnrollmentJournal::Record current{};
  if (!ReplacementGuardReserve::samePrepared(operation, operation) ||
      storage.read(current) != EnrollmentJournal::ReadStatus::Empty) return Outcome::Held;
  bool occupied = false;
  if (sensor.inspectSlot(operation.slot, occupied) != EnrollmentSensor::Ok || !occupied)
    return Outcome::Refused;
  if (!sensor.prepareModel()) return Outcome::CaptureFailure;
  if (ReplacementGuardReserve::reserve(storage, reserve, operation) !=
      ReplacementGuardReserve::Result::ReceiptVerifiedHeld) return Outcome::Held;
  switch (ReplacementWrite::writeOnce(storage, sensor, grant, operation)) {
    case ReplacementWrite::Result::Refused:
    case ReplacementWrite::Result::JournalFailure:
      return Outcome::Held;
    case ReplacementWrite::Result::SensorUncertain:
      return Outcome::Uncertain;
    case ReplacementWrite::Result::Confirmed:
      return ReplacementFinish::finish(storage, finish) == ReplacementFinish::Result::Completed
          ? Outcome::Completed : Outcome::CompletionPending;
  }
  return Outcome::Held;
}
}
