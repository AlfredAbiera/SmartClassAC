#pragma once

#include <cstdio>

namespace ScanResultNotice {
constexpr unsigned long NameDurationMs = 2000;

struct Message {
  char line0[17];
  char line1[17];
};

struct Sequence {
  Message name;
  Message result;
  bool hasName;
};

inline Sequence make(const char* teacherName, const char* line0, const char* line1) {
  Sequence sequence{};
  sequence.hasName = teacherName != nullptr && teacherName[0] != '\0';
  std::snprintf(sequence.name.line0, sizeof(sequence.name.line0), "%s", "Matched teacher");
  std::snprintf(sequence.name.line1, sizeof(sequence.name.line1), "%s", sequence.hasName ? teacherName : "");
  std::snprintf(sequence.result.line0, sizeof(sequence.result.line0), "%s", line0);
  std::snprintf(sequence.result.line1, sizeof(sequence.result.line1), "%s", line1);
  return sequence;
}

inline const Message& first(const Sequence& sequence) {
  return sequence.hasName ? sequence.name : sequence.result;
}
}
