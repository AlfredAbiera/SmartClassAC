#pragma once

#include <stddef.h>
#include <stdint.h>

namespace EnrollmentInventory {
constexpr uint16_t MaxCapacity = 2048;
constexpr size_t MaxBitmapBytes = MaxCapacity / 8;

template <typename Sensor>
uint8_t read(Sensor& sensor, uint16_t& capacity, uint8_t (&bitmap)[MaxBitmapBytes], size_t& length) {
  capacity = 0;
  length = 0;
  uint8_t result = sensor.getParameters(capacity);
  if (result != 0) return result;
  if (capacity < 2 || capacity > MaxCapacity) return 0xfe;
  length = (capacity + 7u) / 8u;
  for (size_t offset = 0; offset < length; offset += 32) {
    uint8_t page[32];
    result = sensor.readIndex(static_cast<uint8_t>(offset / 32), page);
    if (result != 0) { length = 0; return result; }
    size_t count = length - offset < 32 ? length - offset : 32;
    for (size_t i = 0; i < count; ++i) bitmap[offset + i] = page[i];
  }
  return 0;
}
}
