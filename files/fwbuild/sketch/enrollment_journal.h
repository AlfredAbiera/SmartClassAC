#line 1 "D:\\SmartClassAC-ESP32\\smartclass\\enrollment_journal.h"
#pragma once

#include <stddef.h>
#include <stdint.h>
#include <string.h>

namespace EnrollmentJournal {
constexpr size_t IdSize = 16;
constexpr size_t RecordSize = 43;
enum class State : uint8_t { Uncertain = 1, Confirmed = 2 };
enum class ReadStatus : uint8_t { Empty, Valid, Error };
enum class WriteResult : uint8_t { Confirmed, JournalError, AlreadyPending, SensorUncertain };

struct Record {
  uint8_t preparation[IdSize];
  uint8_t operation[IdSize];
  uint16_t slot;
  State state;
};

inline uint32_t checksum(const uint8_t* bytes, size_t length) {
  uint32_t crc = 0xffffffffUL;
  for (size_t i = 0; i < length; ++i) {
    crc ^= bytes[i];
    for (int bit = 0; bit < 8; ++bit)
      crc = (crc >> 1) ^ (0xedb88320UL & (0UL - (crc & 1)));
  }
  return ~crc;
}

inline bool validId(const uint8_t* id) {
  uint8_t any = 0;
  for (size_t i = 0; i < IdSize; ++i) any |= id[i];
  return any != 0;
}

inline bool valid(const Record& record) {
  return validId(record.preparation) && validId(record.operation) && record.slot != 0 &&
      (record.state == State::Uncertain || record.state == State::Confirmed);
}

inline void encode(const Record& record, uint8_t* bytes) {
  bytes[0] = 'S'; bytes[1] = 'C'; bytes[2] = 1;
  bytes[3] = static_cast<uint8_t>(record.state);
  bytes[4] = static_cast<uint8_t>(record.slot >> 8);
  bytes[5] = static_cast<uint8_t>(record.slot);
  memcpy(bytes + 6, record.preparation, IdSize);
  memcpy(bytes + 22, record.operation, IdSize);
  uint32_t crc = checksum(bytes, 38);
  for (int i = 0; i < 4; ++i) bytes[38 + i] = static_cast<uint8_t>(crc >> (24 - 8 * i));
  bytes[42] = 0;
}

inline bool decode(const uint8_t* bytes, Record& record) {
  if (bytes[0] != 'S' || bytes[1] != 'C' || bytes[2] != 1 || bytes[42] != 0) return false;
  uint32_t stored = 0;
  for (int i = 0; i < 4; ++i) stored = (stored << 8) | bytes[38 + i];
  if (stored != checksum(bytes, 38)) return false;
  record.state = static_cast<State>(bytes[3]);
  record.slot = (static_cast<uint16_t>(bytes[4]) << 8) | bytes[5];
  memcpy(record.preparation, bytes + 6, IdSize);
  memcpy(record.operation, bytes + 22, IdSize);
  return valid(record);
}

// The caller must have authenticated authorization and a durable server guard.
// The sensor adapter must validate the entire acknowledgement, including its
// checksum; Adafruit_Fingerprint::storeModel does not validate that checksum.
// Storage must atomically replace the complete record and verify it after writing.
template <typename Storage, typename Sensor>
WriteResult writeOnce(Storage& storage, Sensor& sensor, Record record) {
  Record existing{};
  ReadStatus status = storage.read(existing);
  if (status == ReadStatus::Error || !valid(record)) return WriteResult::JournalError;
  if (status != ReadStatus::Empty) return WriteResult::AlreadyPending;
  record.state = State::Uncertain;
  if (!storage.write(record)) return WriteResult::JournalError;
  if (sensor.storeModel(record.slot) != 0) return WriteResult::SensorUncertain;
  record.state = State::Confirmed;
  if (!storage.write(record)) return WriteResult::JournalError;
  return WriteResult::Confirmed;
}

template <typename Storage>
bool clearConfirmed(Storage& storage) {
  Record record{};
  if (storage.read(record) != ReadStatus::Valid || record.state != State::Confirmed) return false;
  return storage.clear();
}
}
