#line 1 "D:\\SmartClassAC-ESP32\\smartclass\\enrollment_sensor.h"
#pragma once

#include <stddef.h>
#include <stdint.h>

namespace EnrollmentSensor {
constexpr uint8_t Ok = 0;
constexpr uint8_t ProtocolError = 0xfe;
constexpr uint8_t Timeout = 0xff;
constexpr uint32_t TimeoutMs = 5000;

// Transport provides available(), read(), and write(byte); clock provides
// now() and pause(ms). A protocol fault permanently poisons this session.
template <typename Transport, typename Clock>
class CheckedSensor {
 public:
  CheckedSensor(Transport& transport, Clock& clock) : transport_(transport), clock_(clock) {}

  uint8_t verifyPassword() {
    const uint8_t cmd[] = {0x13, 0, 0, 0, 0};
    uint8_t reply[1];
    return command(cmd, sizeof(cmd), reply, sizeof(reply));
  }

  uint8_t getParameters(uint16_t& capacity) {
    capacity = 0;
    const uint8_t cmd[] = {0x0f};
    uint8_t reply[17];
    uint8_t result = command(cmd, sizeof(cmd), reply, sizeof(reply));
    if (result != Ok) return result;
    uint16_t value = (static_cast<uint16_t>(reply[5]) << 8) | reply[6];
    if (value == 0) return fault();
    capacity = value;
    return Ok;
  }

  uint8_t readIndex(uint8_t page, uint8_t (&bitmap)[32]) {
    const uint8_t cmd[] = {0x1f, page};
    uint8_t reply[33];
    uint8_t result = command(cmd, sizeof(cmd), reply, sizeof(reply));
    if (result != Ok) return result;
    for (size_t i = 0; i < sizeof(bitmap); ++i) bitmap[i] = reply[i + 1];
    return Ok;
  }

  // Reports the sensor's index bit only; callers must separately obtain
  // explicit overwrite authorization and prevent concurrent sensor changes.
  uint8_t inspectSlot(uint16_t slot, bool& occupied) {
    uint16_t capacity = 0;
    uint8_t result = getParameters(capacity);
    if (result != Ok) return result;
    if (slot == 0 || slot >= capacity || slot / 256 > 255) return ProtocolError;
    uint8_t bitmap[32];
    result = readIndex(static_cast<uint8_t>(slot / 256), bitmap);
    if (result != Ok) return result;
    occupied = (bitmap[(slot % 256) / 8] & (1u << (slot % 8))) != 0;
    return Ok;
  }

  uint8_t getImage() {
    const uint8_t cmd[] = {0x01};
    uint8_t reply[1];
    return command(cmd, sizeof(cmd), reply, sizeof(reply));
  }

  uint8_t image2Tz(uint8_t buffer) {
    if (buffer != 1 && buffer != 2) return ProtocolError;
    const uint8_t cmd[] = {0x02, buffer};
    uint8_t reply[1];
    return command(cmd, sizeof(cmd), reply, sizeof(reply));
  }

  uint8_t createModel() {
    const uint8_t cmd[] = {0x05};
    uint8_t reply[1];
    return command(cmd, sizeof(cmd), reply, sizeof(reply));
  }

  uint8_t storeModel(uint16_t slot) {
    if (slot == 0) return ProtocolError;
    const uint8_t cmd[] = {0x06, 0x01, static_cast<uint8_t>(slot >> 8), static_cast<uint8_t>(slot)};
    uint8_t reply[1];
    return command(cmd, sizeof(cmd), reply, sizeof(reply));
  }

 private:
  Transport& transport_;
  Clock& clock_;
  bool poisoned_ = false;

  uint8_t fault() {
    poisoned_ = true;
    return ProtocolError;
  }

  uint8_t command(const uint8_t* cmd, size_t commandLength, uint8_t* reply, size_t successLength) {
    if (poisoned_ || transport_.available()) return fault();
    const uint16_t wireLength = static_cast<uint16_t>(commandLength + 2);
    const uint8_t header[] = {0xef, 0x01, 0xff, 0xff, 0xff, 0xff, 0x01,
                              static_cast<uint8_t>(wireLength >> 8), static_cast<uint8_t>(wireLength)};
    uint16_t sum = 0x01 + wireLength;
    for (uint8_t b : header) if (transport_.write(b) != 1) return fault();
    for (size_t i = 0; i < commandLength; ++i) {
      if (transport_.write(cmd[i]) != 1) return fault();
      sum += cmd[i];
    }
    if (transport_.write(static_cast<uint8_t>(sum >> 8)) != 1 ||
        transport_.write(static_cast<uint8_t>(sum)) != 1) return fault();

    uint32_t started = clock_.now();
    uint8_t packetHeader[9];
    for (uint8_t& b : packetHeader) if (!readByte(b, started)) return timeout();
    if (packetHeader[0] != 0xef || packetHeader[1] != 0x01 ||
        packetHeader[2] != 0xff || packetHeader[3] != 0xff ||
        packetHeader[4] != 0xff || packetHeader[5] != 0xff ||
        packetHeader[6] != 0x07) return fault();
    uint16_t length = (static_cast<uint16_t>(packetHeader[7]) << 8) | packetHeader[8];
    if (length < 3 || length > successLength + 2) return fault();
    const size_t payloadLength = length - 2;
    sum = 0x07 + packetHeader[7] + packetHeader[8];
    for (size_t i = 0; i < payloadLength; ++i) {
      if (!readByte(reply[i], started)) return timeout();
      sum += reply[i];
    }
    uint8_t high, low;
    if (!readByte(high, started) || !readByte(low, started)) return timeout();
    if (sum != ((static_cast<uint16_t>(high) << 8) | low)) return fault();
    if ((reply[0] == Ok && payloadLength != successLength) ||
        (reply[0] != Ok && payloadLength != 1)) return fault();
    return reply[0];
  }

  bool readByte(uint8_t& value, uint32_t started) {
    while (static_cast<uint32_t>(clock_.now() - started) < TimeoutMs) {
      if (transport_.available()) {
        int next = transport_.read();
        if (next < 0 || next > 255) return false;
        value = static_cast<uint8_t>(next);
        return true;
      }
      clock_.pause(1);
    }
    return false;
  }

  uint8_t timeout() {
    poisoned_ = true;
    return Timeout;
  }
};
}
