#include "../../esp32/smartclass/fingerprint_deletion.h"
#include <cassert>
#include <cstdio>
#include <cstring>

struct Storage {
  EnrollmentJournal::ReadStatus status = EnrollmentJournal::ReadStatus::Empty;
  uint8_t bytes[EnrollmentJournal::RecordSize]{};
  bool failWrite = false, failClear = false;
  EnrollmentJournal::ReadStatus read(EnrollmentJournal::Record& record) {
    if (status != EnrollmentJournal::ReadStatus::Valid) return status;
    return EnrollmentJournal::decode(bytes, record) ? status : EnrollmentJournal::ReadStatus::Error;
  }
  bool write(const EnrollmentJournal::Record& record) {
    if (failWrite) return false;
    EnrollmentJournal::encode(record, bytes);
    status = EnrollmentJournal::ReadStatus::Valid;
    return true;
  }
  bool clear() {
    if (failClear) return false;
    status = EnrollmentJournal::ReadStatus::Empty;
    return true;
  }
};

struct Sensor {
  bool occupied = true, failDelete = false, failAfter = false, retainAfterDelete = false;
  int deletes = 0, inspections = 0;
  uint8_t inspectSlot(uint16_t slot, bool& value) {
    assert(slot == 7);
    inspections++;
    value = occupied;
    return failAfter && deletes > 0 ? 1 : 0;
  }
  uint8_t deleteModel(uint16_t slot) {
    assert(slot == 7);
    deletes++;
    if (!retainAfterDelete) occupied = false;
    return failDelete ? 1 : 0;
  }
};

struct Adapter {
  bool auth = true, grant = true, finishAvailable = true;
  int claims = 0, finishes = 0;
  bool getChallenge(const char*, uint8_t* challenge) { memset(challenge, 1, 32); return true; }
  bool makeNonce(char* nonce) { memset(nonce, 'a', 64); nonce[64] = '\0'; return true; }
  bool sign(const char*, const uint8_t*, const uint8_t*, size_t, uint8_t* tag) { memset(tag, 1, 32); return true; }
  bool verify(const char*, const uint8_t*, const uint8_t*, size_t, const uint8_t*) { return auth; }
  bool exchange(const char* action, const char* id, uint16_t slot, const char* nonce,
                const uint8_t*, const uint8_t*, uint8_t* body, size_t capacity, size_t& length, uint8_t* tag) {
    memset(tag, 1, 32);
    int written;
    if (strcmp(action, "operation") == 0) {
      written = snprintf(reinterpret_cast<char*>(body), capacity, "11111111-1111-1111-1111-111111111111\n7\n%s\ndelete\n", nonce);
    } else if (strcmp(action, "claim") == 0) {
      claims++;
      if (!grant) return false;
      written = snprintf(reinterpret_cast<char*>(body), capacity, "%s\n%u\n%s\nclaim\ngranted\n", id, slot, nonce);
    } else {
      finishes++;
      if (!finishAvailable) return false;
      written = snprintf(reinterpret_cast<char*>(body), capacity, "%s\n%u\n%s\nconfirmed\ncompleted\n", id, slot, nonce);
    }
    if (written <= 0 || static_cast<size_t>(written) >= capacity) return false;
    length = static_cast<size_t>(written);
    return true;
  }
};

int main() {
  using namespace FingerprintDeletion;
  Adapter adapter;
  EnrollmentJournal::Record operation{};
  assert(discover(adapter, operation));
  assert(operation.kind == EnrollmentJournal::Kind::Deletion && operation.slot == 7);
  adapter.auth = false;
  EnrollmentJournal::Record rejected{};
  assert(!discover(adapter, rejected));
  adapter.auth = true;

  Storage storage;
  Sensor sensor;
  assert(runOnce(storage, sensor, adapter, operation) == Result::Completed);
  assert(sensor.deletes == 1 && sensor.inspections == 2 && adapter.claims == 1 && adapter.finishes == 1);
  assert(storage.status == EnrollmentJournal::ReadStatus::Empty);

  Storage refusedStorage;
  Sensor emptySensor; emptySensor.occupied = false;
  assert(runOnce(refusedStorage, emptySensor, adapter, operation) == Result::Refused);
  assert(emptySensor.deletes == 0 && refusedStorage.status == EnrollmentJournal::ReadStatus::Empty);

  Storage lostClaim;
  Sensor untouched;
  Adapter lost; lost.grant = false;
  assert(runOnce(lostClaim, untouched, lost, operation) == Result::Held);
  assert(untouched.deletes == 0);
  lost.grant = true;
  assert(runOnce(lostClaim, untouched, lost, operation) == Result::Held);
  assert(untouched.deletes == 0 && lost.claims == 1);
  assert(finish(lostClaim, lost) == Result::Held);

  Storage badSignature;
  Sensor protectedSensor;
  Adapter forged; forged.auth = false;
  assert(runOnce(badSignature, protectedSensor, forged, operation) == Result::Held);
  assert(protectedSensor.deletes == 0);

  Storage uncertain;
  Sensor badAck; badAck.failDelete = true;
  Adapter once;
  assert(runOnce(uncertain, badAck, once, operation) == Result::Uncertain);
  assert(runOnce(uncertain, badAck, once, operation) == Result::Held);
  assert(badAck.deletes == 1 && once.claims == 1);
  assert(finish(uncertain, once) == Result::Held);

  Storage notVerified;
  Sensor badIndex; badIndex.failAfter = true;
  assert(runOnce(notVerified, badIndex, once, operation) == Result::Uncertain);
  assert(finish(notVerified, once) == Result::Held);

  Storage stillOccupied;
  Sensor failedDelete; failedDelete.retainAfterDelete = true;
  assert(runOnce(stillOccupied, failedDelete, once, operation) == Result::Uncertain);
  assert(finish(stillOccupied, once) == Result::Held);

  Storage pending;
  Sensor completed;
  Adapter offline; offline.finishAvailable = false;
  assert(runOnce(pending, completed, offline, operation) == Result::CompletionPending);
  assert(completed.deletes == 1);
  offline.finishAvailable = true;
  assert(finish(pending, offline) == Result::Completed);
  assert(completed.deletes == 1);

  Storage writeFailure; writeFailure.failWrite = true;
  Sensor neverWritten;
  assert(runOnce(writeFailure, neverWritten, adapter, operation) == Result::Held);
  assert(neverWritten.deletes == 0);
  puts("Fingerprint deletion protocol scenarios passed");
}
