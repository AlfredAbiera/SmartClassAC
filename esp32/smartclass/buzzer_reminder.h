#pragma once
#include <stddef.h>
#include <string.h>

namespace BuzzerReminder {
inline bool validId(const char* id) {
  if (!id) return false;
  size_t len = strlen(id);
  if (len < 16 || len > 34) return false;
  size_t dash = 0;
  while (dash < len && id[dash] >= '0' && id[dash] <= '9') ++dash;
  if (dash == 0 || dash > 19 || dash + 15 != len || id[dash] != '-') return false;
  for (size_t i = dash + 1; i < len; ++i)
    if (id[i] < '0' || id[i] > '9') return false;
  return true;
}

inline bool shouldPlay(const char* id, const char* previous) {
  return validId(id) && (!previous || strcmp(id, previous) != 0);
}
}
