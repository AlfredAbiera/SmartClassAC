#pragma once
#include <stddef.h>
#include <stdint.h>
#include <string.h>

namespace FacultyMenu {
enum class Result : uint8_t { Denied, Accepted, End, Item };
enum class TargetResult : uint8_t { Saved, Denied, NoClass, Minimum, Stale };
inline bool decodeJsonBody(const char* escaped, size_t length, char* out, size_t capacity, size_t& written) {
  if (!escaped || !out || capacity == 0) return false;
  size_t n = 0;
  for (size_t i = 0; i < length; ++i) {
    unsigned char ch = static_cast<unsigned char>(escaped[i]);
    if (ch == '\\') {
      if (++i >= length) return false;
      ch = static_cast<unsigned char>(escaped[i]);
      if (ch == 'n') ch = '\n';
      else if (ch == 'u') {
        if (i + 4 >= length || escaped[i + 1] != '0' || escaped[i + 2] != '0') return false;
        unsigned value = 0;
        for (size_t j = 3; j <= 4; ++j) {
          char digit = escaped[i + j];
          if (digit >= '0' && digit <= '9') value = value * 16 + static_cast<unsigned>(digit - '0');
          else if (digit >= 'a' && digit <= 'f') value = value * 16 + static_cast<unsigned>(digit - 'a' + 10);
          else if (digit >= 'A' && digit <= 'F') value = value * 16 + static_cast<unsigned>(digit - 'A' + 10);
          else return false;
        }
        ch = static_cast<unsigned char>(value);
        i += 4;
      } else if (ch != '"' && ch != '\\' && ch != '/') return false;
    } else if (ch == '"' || ch < 32 || ch > 126) return false;
    if ((ch < 32 && ch != '\n') || ch > 126 || n + 1 >= capacity) return false;
    out[n++] = static_cast<char>(ch);
  }
  out[n] = '\0';
  written = n;
  return true;
}
struct Schedule {
  unsigned index;
  unsigned count;
  char subject[17];
  char room[17];
  char start[6];
  char end[6];
};
struct AcInfo {
  uint64_t room;
  unsigned target;
  unsigned minimum;
  bool active;
};
inline bool roomNumber(const char* value, uint64_t& result) {
  if (!value || !*value) return false;
  uint64_t n = 0;
  for (const char* p = value; *p; ++p) {
    if (*p < '0' || *p > '9') return false;
    unsigned digit = static_cast<unsigned>(*p - '0');
    if (n > (UINT64_C(9223372036854775807) - digit) / 10) return false;
    n = n * 10 + digit;
  }
  if (!n) return false;
  result = n;
  return true;
}
inline bool hexToken(const char* value, size_t length) {
  if (!value || length != 64) return false;
  for (size_t i = 0; i < length; ++i)
    if (!((value[i] >= '0' && value[i] <= '9') || (value[i] >= 'a' && value[i] <= 'f'))) return false;
  return true;
}
inline bool line(const char*& p, const char* end, char* out, size_t size) {
  size_t n = 0;
  while (p < end && *p != '\n') {
    if (static_cast<unsigned char>(*p) < 32 || static_cast<unsigned char>(*p) > 126 || n + 1 >= size) return false;
    out[n++] = *p++;
  }
  if (p == end) return false;
  ++p;
  out[n] = '\0';
  return true;
}
inline bool number(const char* value, unsigned& result) {
  if (!*value) return false;
  unsigned n = 0;
  for (const char* p = value; *p; ++p) {
    if (*p < '0' || *p > '9' || n > 99999) return false;
    n = n * 10 + static_cast<unsigned>(*p - '0');
  }
  result = n;
  return true;
}
inline bool timeValue(const char* value) {
  return strlen(value) == 5 && value[2] == ':' &&
      value[0] >= '0' && value[0] <= '2' && value[1] >= '0' && value[1] <= '9' &&
      (value[0] != '2' || value[1] <= '3') &&
      value[3] >= '0' && value[3] <= '5' && value[4] >= '0' && value[4] <= '9';
}
template <typename Verify>
bool parseAc(const char* digest, Verify verify, const uint8_t* body, size_t length,
             const uint8_t* tag, size_t tagLength, Result& result, AcInfo& info) {
  if (!digest || strlen(digest) != 64 || !body || !tag || tagLength != 32 || length < 69 ||
      memcmp(body, digest, 64) != 0 || body[64] != '\n' || !verify(body, length, tag, tagLength)) return false;
  const char* p = reinterpret_cast<const char*>(body) + 65;
  const char* end = reinterpret_cast<const char*>(body) + length;
  char status[10], room[20], target[4], minimum[4], active[2];
  if (!line(p, end, status, sizeof(status))) return false;
  if (strcmp(status, "denied") == 0 && p == end) { result = Result::Denied; return true; }
  AcInfo candidate{};
  if (strcmp(status, "info") != 0 || !line(p, end, room, sizeof(room)) || !roomNumber(room, candidate.room) ||
      !line(p, end, target, sizeof(target)) ||
      !line(p, end, minimum, sizeof(minimum)) || !line(p, end, active, sizeof(active)) || p != end ||
      !number(target, candidate.target) || !number(minimum, candidate.minimum) ||
      candidate.target < 16 || candidate.target > 30 || candidate.minimum < 16 || candidate.minimum > 30 ||
      (strcmp(active, "0") != 0 && strcmp(active, "1") != 0)) return false;
  candidate.active = active[0] == '1';
  info = candidate;
  result = Result::Item;
  return true;
}
template <typename Verify>
bool parseTarget(const char* digest, Verify verify, const uint8_t* body, size_t length,
                 const uint8_t* tag, size_t tagLength, TargetResult& result) {
  if (!digest || strlen(digest) != 64 || !body || !tag || tagLength != 32 || length < 69 ||
      memcmp(body, digest, 64) != 0 || body[64] != '\n' || !verify(body, length, tag, tagLength)) return false;
  const char* p = reinterpret_cast<const char*>(body) + 65;
  const char* end = reinterpret_cast<const char*>(body) + length;
  char status[10];
  if (!line(p, end, status, sizeof(status)) || p != end) return false;
  if (strcmp(status, "saved") == 0) result = TargetResult::Saved;
  else if (strcmp(status, "denied") == 0) result = TargetResult::Denied;
  else if (strcmp(status, "no_class") == 0) result = TargetResult::NoClass;
  else if (strcmp(status, "minimum") == 0) result = TargetResult::Minimum;
  else if (strcmp(status, "stale") == 0) result = TargetResult::Stale;
  else return false;
  return true;
}
template <typename Verify>
bool parse(bool login, unsigned requestedIndex, const char* digest, Verify verify,
           const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength,
           Result& result, char* token, Schedule& schedule) {
  if (!digest || strlen(digest) != 64 || !body || !tag || tagLength != 32 || length < 69 ||
      memcmp(body, digest, 64) != 0 || body[64] != '\n') return false;
  if (!verify(body, length, tag, tagLength)) return false;
  const char* p = reinterpret_cast<const char*>(body) + 65;
  const char* end = reinterpret_cast<const char*>(body) + length;
  char status[10];
  if (!line(p, end, status, sizeof(status))) return false;
  if (strcmp(status, "denied") == 0 && p == end) { result = Result::Denied; return true; }
  if (login) {
    char candidate[65];
    if (strcmp(status, "accepted") != 0 || !token || !line(p, end, candidate, sizeof(candidate)) ||
        p != end || !hexToken(candidate, strlen(candidate))) return false;
    memcpy(token, candidate, sizeof(candidate));
    result = Result::Accepted;
    return true;
  }
  if (strcmp(status, "end") == 0 && p == end) { result = Result::End; return true; }
  Schedule candidate{};
  char index[8], count[8];
  if (strcmp(status, "item") != 0 || !line(p, end, index, sizeof(index)) ||
      !line(p, end, count, sizeof(count)) || !number(index, candidate.index) ||
      !number(count, candidate.count) || candidate.index != requestedIndex ||
      candidate.count <= candidate.index || !line(p, end, candidate.subject, sizeof(candidate.subject)) ||
      !line(p, end, candidate.room, sizeof(candidate.room)) || !line(p, end, candidate.start, sizeof(candidate.start)) ||
      !line(p, end, candidate.end, sizeof(candidate.end)) || p != end ||
      !timeValue(candidate.start) || !timeValue(candidate.end)) return false;
  schedule = candidate;
  result = Result::Item;
  return true;
}
}
