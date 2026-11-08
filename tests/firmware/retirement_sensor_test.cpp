#include "../../esp32/smartclass/enrollment_sensor.h"
#include <assert.h>
#include <deque>
#include <iostream>
#include <vector>

struct Clock {
  uint32_t time = 0;
  uint32_t now() { return time; }
  void pause(uint32_t ms) { time += ms; }
};

struct Transport {
  std::deque<uint8_t> input;
  std::vector<uint8_t> request, commands;
  uint16_t capacity = 300, count = 0;
  int badChecksumCommand = -1, rejectedCommand = -1, occupiedPage = -1;
  bool dropClearReply = false;
  int available() { return static_cast<int>(input.size()); }
  int read() { int v = input.front(); input.pop_front(); return v; }
  size_t write(uint8_t v) {
    request.push_back(v);
    if (request.size() >= 9) {
      size_t length = (static_cast<size_t>(request[7]) << 8) | request[8];
      if (request.size() == 9 + length) respond();
    }
    return 1;
  }
  void respond() {
    uint8_t command = request[9];
    commands.push_back(command);
    std::vector<uint8_t> payload{0};
    if (command == rejectedCommand) payload[0] = 1;
    else if (command == 0x0f) {
      payload.resize(17);
      payload[5] = static_cast<uint8_t>(capacity >> 8);
      payload[6] = static_cast<uint8_t>(capacity);
    } else if (command == 0x1d) {
      payload = {0, static_cast<uint8_t>(count >> 8), static_cast<uint8_t>(count)};
    } else if (command == 0x1f) {
      payload.resize(33);
      if (request[10] == occupiedPage) payload[1] = 1;
    }
    request.clear();
    if (command == 0x0d && dropClearReply) return;
    uint16_t length = static_cast<uint16_t>(payload.size() + 2);
    uint16_t sum = 7 + length;
    for (int b : {0xef, 1, 0xff, 0xff, 0xff, 0xff, 7, 0, static_cast<int>(length)})
      input.push_back(static_cast<uint8_t>(b));
    for (uint8_t b : payload) { input.push_back(b); sum += b; }
    if (command == badChecksumCommand) ++sum;
    input.push_back(static_cast<uint8_t>(sum >> 8));
    input.push_back(static_cast<uint8_t>(sum));
  }
};

uint8_t clear(Transport& transport) {
  Clock clock;
  EnrollmentSensor::CheckedSensor<Transport, Clock> sensor(transport, clock);
  return sensor.clearAndVerifyEmpty();
}

int main() {
  Transport ok;
  assert(clear(ok) == EnrollmentSensor::Ok);
  assert((ok.commands == std::vector<uint8_t>{0x13, 0x0f, 0x0d, 0x1d, 0x1f, 0x1f}));
  Transport badAck; badAck.badChecksumCommand = 0x0d;
  assert(clear(badAck) == EnrollmentSensor::ProtocolError);
  assert(badAck.commands.size() == 3);
  Transport rejected; rejected.rejectedCommand = 0x0d;
  assert(clear(rejected) != EnrollmentSensor::Ok);
  Transport lost; lost.dropClearReply = true;
  assert(clear(lost) == EnrollmentSensor::Timeout);
  Transport occupied; occupied.count = 2;
  assert(clear(occupied) == EnrollmentSensor::ProtocolError);
  Transport bitmap; bitmap.occupiedPage = 1;
  assert(clear(bitmap) == EnrollmentSensor::ProtocolError);
  Transport oversized; oversized.capacity = 2049;
  assert(clear(oversized) == EnrollmentSensor::ProtocolError);
  assert(oversized.commands.size() == 2);
  Transport invalid; invalid.capacity = 0;
  assert(clear(invalid) == EnrollmentSensor::ProtocolError);
  Transport full; full.capacity = 2048;
  assert(clear(full) == EnrollmentSensor::Ok);
  assert(full.commands.size() == 12);
  std::cout << "9 retirement sensor scenarios passed\n";
}
