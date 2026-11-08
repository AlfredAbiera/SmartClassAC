#pragma once

#include <stdint.h>

namespace EnrollmentCapture {
constexpr uint8_t Ok = 0x00;
constexpr uint8_t NoFinger = 0x02;
constexpr uint32_t PollIntervalMs = 50;
constexpr uint32_t CaptureTimeoutMs = 30000;

enum class Stage : uint8_t { FirstFinger, RemoveFinger, SecondFinger };
enum class Status : uint8_t { ModelReady, TimedOut, SensorError };

struct Result {
  Status status;
  Stage stage;
  uint8_t sensorCode;
};

// Sensor calls must validate acknowledgements and bound their UART wait.
template <typename Sensor, typename Clock, typename Display>
Result captureModel(Sensor& sensor, Clock& clock, Display& display) {
  const Stage stages[] = {Stage::FirstFinger, Stage::RemoveFinger, Stage::SecondFinger};
  for (Stage stage : stages) {
    display.show(stage);
    uint32_t start = clock.now();
    bool complete = false;
    uint8_t lastCode = NoFinger;
    while (static_cast<uint32_t>(clock.now() - start) < CaptureTimeoutMs) {
      uint8_t code = sensor.getImage();
      lastCode = code;
      if (static_cast<uint32_t>(clock.now() - start) >= CaptureTimeoutMs)
        return {Status::TimedOut, stage, code};
      if ((stage == Stage::RemoveFinger && code == NoFinger) ||
          (stage != Stage::RemoveFinger && code == Ok)) {
        complete = true;
        break;
      }
      if (code != Ok && code != NoFinger)
        return {Status::SensorError, stage, code};
      clock.pause(PollIntervalMs);
    }
    if (!complete) return {Status::TimedOut, stage, lastCode};
    if (stage != Stage::RemoveFinger) {
      uint8_t code = sensor.image2Tz(stage == Stage::FirstFinger ? 1 : 2);
      if (code != Ok) return {Status::SensorError, stage, code};
    }
  }
  uint8_t code = sensor.createModel();
  return code == Ok ? Result{Status::ModelReady, Stage::SecondFinger, code}
                    : Result{Status::SensorError, Stage::SecondFinger, code};
}
}
