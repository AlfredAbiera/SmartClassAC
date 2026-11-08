#pragma once

#include <stddef.h>
#include <stdint.h>
#include <string.h>

namespace EnrollmentJournal {
constexpr size_t IdSize = 16;
constexpr size_t RecordSize = 43;
constexpr size_t AttemptRecordSize = 59;
enum class State : uint8_t {
  Uncertain = 1, Confirmed = 2, ReplacementPrepared = 3, ReplacementFenced = 4,
  RecoveryPrepared = 5, RecoveryFenced = 6
};
enum class Kind : uint8_t { NewEnrollment = 0, Replacement = 1, ReplacementAttempt = 2 };
enum class ReadStatus : uint8_t { Empty, Valid, Error };
enum class WriteResult : uint8_t { Confirmed, JournalError, AlreadyPending, SensorUncertain };

struct Record {
  uint8_t preparation[IdSize];
  uint8_t operation[IdSize];
  uint16_t slot;
  State state;
  Kind kind = Kind::NewEnrollment;
  uint8_t recoveryOperation[IdSize] = {};
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

inline bool formatId(const uint8_t* id, char* output) {
  if (!id || !output || !validId(id)) return false;
  const char digits[] = "0123456789abcdef";
  size_t position = 0;
  for (size_t i = 0; i < IdSize; ++i) {
    if (i == 4 || i == 6 || i == 8 || i == 10) output[position++] = '-';
    output[position++] = digits[id[i] >> 4];
    output[position++] = digits[id[i] & 15];
  }
  output[position] = '\0';
  return true;
}

inline bool valid(const Record& record) {
  bool hasRecovery = validId(record.recoveryOperation);
  return validId(record.preparation) && validId(record.operation) && record.slot != 0 &&
      ((record.kind == Kind::NewEnrollment && !hasRecovery &&
        (record.state == State::Uncertain || record.state == State::Confirmed)) ||
       (record.kind == Kind::Replacement && !hasRecovery &&
        (record.state == State::Uncertain || record.state == State::Confirmed ||
         record.state == State::ReplacementPrepared || record.state == State::ReplacementFenced)) ||
       (record.kind == Kind::ReplacementAttempt &&
        (record.state == State::Uncertain || record.state == State::RecoveryPrepared ||
         record.state == State::RecoveryFenced ||
         record.state == State::Confirmed) &&
        hasRecovery && memcmp(record.operation, record.recoveryOperation, IdSize) != 0));
}

inline void encode(const Record& record, uint8_t* bytes) {
  bytes[0] = 'S'; bytes[1] = 'C'; bytes[2] = record.kind == Kind::Replacement ? 2 : 1;
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
  if (bytes[0] != 'S' || bytes[1] != 'C' || (bytes[2] != 1 && bytes[2] != 2) || bytes[42] != 0) return false;
  uint32_t stored = 0;
  for (int i = 0; i < 4; ++i) stored = (stored << 8) | bytes[38 + i];
  if (stored != checksum(bytes, 38)) return false;
  record.state = static_cast<State>(bytes[3]);
  record.kind = bytes[2] == 1 ? Kind::NewEnrollment : Kind::Replacement;
  memset(record.recoveryOperation, 0, IdSize);
  record.slot = (static_cast<uint16_t>(bytes[4]) << 8) | bytes[5];
  memcpy(record.preparation, bytes + 6, IdSize);
  memcpy(record.operation, bytes + 22, IdSize);
  return valid(record);
}

inline void encodeAttempt(const Record& record, uint8_t* bytes) {
  bytes[0] = 'S'; bytes[1] = 'C'; bytes[2] = 3;
  bytes[3] = static_cast<uint8_t>(record.state);
  bytes[4] = static_cast<uint8_t>(record.slot >> 8);
  bytes[5] = static_cast<uint8_t>(record.slot);
  memcpy(bytes + 6, record.preparation, IdSize);
  memcpy(bytes + 22, record.operation, IdSize);
  memcpy(bytes + 38, record.recoveryOperation, IdSize);
  uint32_t crc = checksum(bytes, 54);
  for (int i = 0; i < 4; ++i) bytes[54 + i] = static_cast<uint8_t>(crc >> (24 - 8 * i));
  bytes[58] = 0;
}

inline bool decodeAttempt(const uint8_t* bytes, Record& record) {
  if (bytes[0] != 'S' || bytes[1] != 'C' || bytes[2] != 3 || bytes[58] != 0) return false;
  uint32_t stored = 0;
  for (int i = 0; i < 4; ++i) stored = (stored << 8) | bytes[54 + i];
  if (stored != checksum(bytes, 54)) return false;
  record.kind = Kind::ReplacementAttempt;
  record.state = static_cast<State>(bytes[3]);
  record.slot = (static_cast<uint16_t>(bytes[4]) << 8) | bytes[5];
  memcpy(record.preparation, bytes + 6, IdSize);
  memcpy(record.operation, bytes + 22, IdSize);
  memcpy(record.recoveryOperation, bytes + 38, IdSize);
  return valid(record);
}

// Persists intent to attempt recovery, not permission to write a sensor.
template <typename Storage>
bool recordRecoveryAttempt(Storage& storage, const Record& expected, const uint8_t* recoveryId) {
  if (!valid(expected) || expected.kind != Kind::Replacement || expected.state != State::ReplacementFenced ||
      !recoveryId || !validId(recoveryId) || memcmp(expected.operation, recoveryId, IdSize) == 0) return false;
  Record current{};
  if (storage.read(current) != ReadStatus::Valid || !valid(current) ||
      current.kind != Kind::Replacement || current.state != State::ReplacementFenced ||
      current.slot != expected.slot || memcmp(current.preparation, expected.preparation, IdSize) != 0 ||
      memcmp(current.operation, expected.operation, IdSize) != 0) return false;
  current.kind = Kind::ReplacementAttempt;
  current.state = State::RecoveryPrepared;
  memcpy(current.recoveryOperation, recoveryId, IdSize);
  if (!storage.write(current)) return false;
  Record verified{};
  return storage.read(verified) == ReadStatus::Valid && valid(verified) &&
      verified.kind == Kind::ReplacementAttempt && verified.state == State::RecoveryPrepared &&
      verified.slot == current.slot &&
      memcmp(verified.preparation, current.preparation, IdSize) == 0 &&
      memcmp(verified.operation, current.operation, IdSize) == 0 &&
      memcmp(verified.recoveryOperation, current.recoveryOperation, IdSize) == 0;
}

// The caller must have verified the prior server fence and the new intent,
// serialize all sensor writes, and discard any prior in-memory grant.
template <typename Storage>
bool recordRecoveryRetry(Storage& storage, const Record& expected, const uint8_t* recoveryId) {
  if (!valid(expected) || expected.kind != Kind::ReplacementAttempt ||
      expected.state != State::RecoveryFenced || !recoveryId || !validId(recoveryId) ||
      memcmp(expected.operation, recoveryId, IdSize) == 0 ||
      memcmp(expected.recoveryOperation, recoveryId, IdSize) == 0) return false;
  Record current{};
  if (storage.read(current) != ReadStatus::Valid || !valid(current) ||
      current.kind != Kind::ReplacementAttempt || current.state != State::RecoveryFenced ||
      current.slot != expected.slot || memcmp(current.preparation, expected.preparation, IdSize) != 0 ||
      memcmp(current.operation, expected.operation, IdSize) != 0 ||
      memcmp(current.recoveryOperation, expected.recoveryOperation, IdSize) != 0) return false;
  current.state = State::RecoveryPrepared;
  memcpy(current.recoveryOperation, recoveryId, IdSize);
  if (!storage.write(current)) return false;
  Record verified{};
  return storage.read(verified) == ReadStatus::Valid && valid(verified) &&
      verified.kind == Kind::ReplacementAttempt && verified.state == State::RecoveryPrepared &&
      verified.slot == current.slot &&
      memcmp(verified.preparation, current.preparation, IdSize) == 0 &&
      memcmp(verified.operation, current.operation, IdSize) == 0 &&
      memcmp(verified.recoveryOperation, current.recoveryOperation, IdSize) == 0;
}

// The caller must have authenticated authorization and a durable server guard.
// The sensor adapter must validate the entire acknowledgement, including its
// checksum; Adafruit_Fingerprint::storeModel does not validate that checksum.
// Storage must atomically replace the complete record and verify it after writing.
template <typename Storage, typename Sensor>
WriteResult writeOnce(Storage& storage, Sensor& sensor, Record record) {
  Record existing{};
  ReadStatus status = storage.read(existing);
  if (status == ReadStatus::Error || !valid(record) || record.kind != Kind::NewEnrollment) return WriteResult::JournalError;
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
  if (storage.read(record) != ReadStatus::Valid || record.state != State::Confirmed ||
      record.kind != Kind::NewEnrollment) return false;
  return storage.clear();
}
}
