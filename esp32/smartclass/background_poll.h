#pragma once
#include <stdint.h>

namespace BackgroundPoll {
enum class Kind : uint8_t { None, Health, Status, Buzzer };

inline bool due(uint32_t now, uint32_t last, uint32_t interval) {
  return static_cast<uint32_t>(now - last) >= interval;
}

inline bool current(uint32_t requestGeneration, uint32_t currentGeneration) {
  return requestGeneration == currentGeneration;
}

inline Kind next(uint32_t now, uint32_t lastHealth, uint32_t healthInterval,
                 uint32_t lastStatus, uint32_t statusInterval,
                 uint32_t lastBuzzer, uint32_t buzzerInterval, bool online, bool paired) {
  if (due(now, lastHealth, healthInterval)) return Kind::Health;
  if (!online || !paired) return Kind::None;
  if (due(now, lastStatus, statusInterval)) return Kind::Status;
  if (due(now, lastBuzzer, buzzerInterval)) return Kind::Buzzer;
  return Kind::None;
}
}
