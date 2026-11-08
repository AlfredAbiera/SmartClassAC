#pragma once
#include <stddef.h>
#include <stdint.h>

namespace BuzzerMelody {
constexpr unsigned long NoteIntervalMs = 500;
constexpr unsigned long NoteDurationMs = 450;
constexpr unsigned int Frequencies[] = {1047, 1319, 1568, 1319, 1175, 1397, 1760, 1397, 1568, 1047};
constexpr size_t NoteCount = sizeof(Frequencies) / sizeof(Frequencies[0]);
constexpr unsigned long DurationMs = NoteCount * NoteIntervalMs;

struct Position {
  unsigned int frequency;
  uint32_t toneDurationMs;
  uint32_t waitMs;
};

inline Position positionAt(uint32_t startMs, uint32_t nowMs) {
  const uint32_t elapsed = nowMs - startMs;
  if (elapsed >= DurationMs) return {0, 0, 0};
  const uint32_t phase = elapsed % NoteIntervalMs;
  const uint32_t remainingTone = phase < NoteDurationMs ? NoteDurationMs - phase : 0;
  return {remainingTone ? Frequencies[elapsed / NoteIntervalMs] : 0,
          remainingTone, NoteIntervalMs - phase};
}
}
