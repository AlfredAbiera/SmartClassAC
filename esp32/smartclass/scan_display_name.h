#pragma once

#include <ArduinoJson.h>
#include <cstddef>

namespace ScanDisplayName {
constexpr size_t Width = 16;

enum class Result { Found, Missing, InvalidResponse };

inline Result read(const char* response, char (&output)[Width + 1]) {
  output[0] = '\0';
  JsonDocument document;
  if (deserializeJson(document, response)) return Result::InvalidResponse;
  const char* name = document["teacherDisplayName"].as<const char*>();
  if (name == nullptr) return Result::Missing;

  while (*name == ' ') ++name;
  size_t length = 0;
  while (*name != '\0' && length < Width) {
    unsigned char ch = static_cast<unsigned char>(*name++);
    if (ch >= 0x80) {
      while ((static_cast<unsigned char>(*name) & 0xC0) == 0x80) ++name;
      output[length++] = '?';
    } else {
      output[length++] = ch >= 0x20 && ch <= 0x7e ? static_cast<char>(ch) : '?';
    }
  }
  while (length > 0 && output[length - 1] == ' ') --length;
  output[length] = '\0';
  return length > 0 ? Result::Found : Result::Missing;
}
}
