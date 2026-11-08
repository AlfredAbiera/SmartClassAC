#pragma once

#include <stddef.h>
#include <stdint.h>
#include <string.h>

namespace FacultyOtp {
enum class Result : uint8_t { Processed, Accepted, Denied };

inline bool validValue(const char* value, bool issuing) {
  if (!value || strlen(value) != (issuing ? 11u : 6u)) return false;
  if (issuing && (value[0] != '0' || value[1] != '9')) return false;
  for (const char* p = value; *p; ++p) if (*p < '0' || *p > '9') return false;
  return true;
}

template <typename Verify>
bool verifyResponse(bool issuing, const char* requestDigest, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength,
                    Result& result) {
  if (!requestDigest || strlen(requestDigest) != 64 || !body || !tag ||
      tagLength != 32 || bodyLength < 66 || memcmp(body, requestDigest, 64) != 0 || body[64] != '\n')
    return false;
  const char* status = reinterpret_cast<const char*>(body + 65);
  size_t statusLength = bodyLength - 65;
  Result candidate;
  if (issuing && statusLength == 10 && memcmp(status, "processed\n", 10) == 0)
    candidate = Result::Processed;
  else if (!issuing && statusLength == 9 && memcmp(status, "accepted\n", 9) == 0)
    candidate = Result::Accepted;
  else if (!issuing && statusLength == 7 && memcmp(status, "denied\n", 7) == 0)
    candidate = Result::Denied;
  else return false;
  if (!verify(body, bodyLength, tag, tagLength)) return false;
  result = candidate;
  return true;
}
}
