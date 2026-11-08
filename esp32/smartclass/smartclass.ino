#include <WiFi.h>
#include <Preferences.h>
#include <HTTPClient.h>
#include <WiFiClientSecure.h>
#include <Adafruit_Fingerprint.h>
#include <Wire.h>
#include <esp_system.h>
#include <LiquidCrystal_I2C.h>
#include <DHT.h>
#include <Keypad.h>
#include "time.h"
#include "esp_sntp.h"
#include "enrollment_auth.h"
#include "enrollment_capture.h"
#include "enrollment_journal.h"
#include "enrollment_sensor.h"
#include "enrollment_recovery.h"
#include "enrollment_step.h"
#include "enrollment_notice.h"
#include "enrollment_inventory.h"
#include "replacement_intent.h"
#include "replacement_attempt_status.h"
#include "replacement_attempt_reserve.h"
#include "replacement_attempt_claim.h"
#include "replacement_attempt_write.h"
#include "replacement_attempt_finish.h"
#include "replacement_operation.h"
#include "replacement_attempt_fence.h"
#include "replacement_step.h"
#include "replacement_finish.h"
#include "replacement_guard_status.h"
#include "replacement_guard_reserve.h"
#include "replacement_write_claim.h"
#include "replacement_write.h"
#include "replacement_write_fence.h"
#include "scan_display_name.h"
#include "scan_result_notice.h"
#include "faculty_otp.h"
#include "faculty_menu.h"
#include "background_poll.h"
#ifndef AC_IR_ENABLED
#define AC_IR_ENABLED 1
#endif
#ifndef AC_IR_POWER_ENABLED
#define AC_IR_POWER_ENABLED 1
#endif
#if AC_IR_ENABLED
#include <IRremoteESP8266.h>
#include <IRsend.h>
#include "ac_ir_protocol.h"
#endif

struct EnrollmentSensorAdapter;

#ifndef ENROLLMENT_WRITES_ENABLED
#define ENROLLMENT_WRITES_ENABLED 1
#endif
#ifndef REPLACEMENT_WRITES_ENABLED
#define REPLACEMENT_WRITES_ENABLED 1
#endif

// ---------------------------------------------------------------------------
// Firmware version — bump on EVERY sketch change (semver: MAJOR.MINOR.PATCH).
// Also print/show via Serial boot banner, LCD splash, and STATUS command.
// Changelog:
//
//   1.6.63 2026-11-08  Add confirmed, guarded local-only unpairing.
//   1.6.62 2026-11-08  Offer guarded pairing after a saved device ID is rejected.
//   1.6.61 2026-11-08  Enable guarded fingerprint enrollment and inventory.
//   1.6.60 2026-11-08  Enable guarded IR power-toggle protocol.
//   1.6.59 2026-11-08  Add opt-in guarded IR power-toggle protocol.
//   1.6.58 2026-11-08  Check for AC IR commands every five seconds.
//   1.6.57 2026-11-08  Move periodic network polling off the keypad loop.
//   1.6.56 2026-11-08  Play a five-second reminder melody.
//   1.6.55 2026-11-08  Add portal-requested buzzer test.
//   1.6.54 2026-11-08  Add schedule-ending buzzer reminders.
//   1.6.53 2026-11-08  Add gated relative IR synchronization.
//   1.6.52 2026-11-08  Add gated Faculty target requests.
//   1.6.51 2026-11-08  Add read-only Faculty AC information.
//   1.6.50 2026-11-08  Decode escaped Faculty schedule names.
//   1.6.49 2026-11-08  Add authenticated Faculty schedule browsing.
//   1.6.48 2026-11-08  Authenticate Faculty login with server SMS OTP.
//   1.6.47 2026-11-08  Remove boot SMS sending.
// ---------------------------------------------------------------------------
const char* FIRMWARE_VERSION = "1.6.63";

// USB Serial Monitor MUST match this baud (115200). Boxes/diamonds = wrong baud
// or fingerprint TX wired to GPIO1/3 (USB UART) instead of FINGERPRINT_RX/TX.
#define SERIAL_BAUD 115200
#define FINGERPRINT_BAUD 57600
#define FINGERPRINT_RX_PIN 16  // ESP RX  <- sensor TX   (NOT GPIO3 / U0RXD)
#define FINGERPRINT_TX_PIN 17  // ESP TX  -> sensor RX   (NOT GPIO1 / U0TXD)
#define GSM_BAUD 9600
#define GSM_RX_PIN 18          // ESP RX <- modem TX
#define GSM_TX_PIN 19          // ESP TX -> modem RX

// ---------------------------------------------------------------------------
// SmartClass device config — set baseUrl to the PC running SmartClassAC
// ---------------------------------------------------------------------------
const char* ssid = "WiFi_Nang_Laroza";
const char* password = "Larozawifi01$$$";
const char* baseUrl = "http://192.168.68.113:5062";  // e.g. http://192.168.x.x:5062
const char* deviceApiKey = "dev-device-key-smartclass";  // must match DeviceApi:ApiKey
// Numeric device ID issued by the server. It is empty until the admin pairs the
// device from its keypad (Admin > Pair device), then it is kept in flash (NVS).
char deviceId[24] = "";
Preferences devicePrefs;

// HTTP is permitted only for an explicitly trusted Development LAN.
// For HTTPS, set this to the PEM CA certificate that validates the portal hostname.
#ifndef FACULTY_OTP_ALLOW_HTTP
#define FACULTY_OTP_ALLOW_HTTP 1
#endif
const char* facultyOtpCaCertificate = "";

// Optional physical AC relay. Set to a free GPIO when wired; -1 = disabled.
// Keypad uses 14, 32, 33, 26, 27, 12, and 25 — do not reuse those.
#define AC_RELAY_PIN -1
#define AC_RELAY_ACTIVE_HIGH 1
#define BUZZER_PIN 13
const unsigned long BUZZER_POLL_MS = 5000;
unsigned long buzzerLastPoll = 0;
TaskHandle_t buzzerMelodyTask = nullptr;
char buzzerLastReminder[35] = {};
#include "buzzer_reminder.h"
#include "buzzer_melody.h"
#if AC_IR_ENABLED
IRsend acIrSender(23);
char acIrBoot[65] = {};
bool acIrRegistered = false;
bool acIrHalted = false;
unsigned long acIrLastPoll = 0;
void pollAcIr();
#endif

// NTP
const char* ntpServer = "pool.ntp.org";
const long  gmtOffset_sec = 8 * 3600;
const int   daylightOffset_sec = 0;

// Hardware
#define DHTPIN 4
#define DHTTYPE DHT22

HardwareSerial gsmSerial(1);
HardwareSerial mySerial(2);
Adafruit_Fingerprint finger = Adafruit_Fingerprint(&mySerial);
LiquidCrystal_I2C lcd(0x27, 16, 2);
DHT dht(DHTPIN, DHTTYPE);

// 4x3 keypad (Arduino Keypad library). Columns use internal pull-ups.
const byte ROWS = 4;
const byte COLS = 3;
char keys[ROWS][COLS] = {
  {'1', '2', '3'},
  {'4', '5', '6'},
  {'7', '8', '9'},
  {'*', '0', '#'}
};
byte rowPins[ROWS] = {14, 32, 33, 26}; // connect to the row pinouts of the keypad
byte colPins[COLS] = {27, 12, 25};     // connect to the column pinouts of the keypad
Keypad keypad = Keypad(makeKeymap(keys), rowPins, colPins, ROWS, COLS);

uint8_t acOnIcon[8] = {
  0b00100, 0b10101, 0b01110, 0b11111, 0b01110, 0b10101, 0b00100, 0b00000
};
uint8_t acOffIcon[8] = {
  0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110, 0b00000
};
// AC state mirrors last successful server started/ended/ac_on/ac_off (not local toggle).
bool isGlobalACOn = false;
unsigned long acStartTime = 0;
char activeTeacherName[17] = "";  // shown on LCD while AC is on
char scannedTeacherName[ScanDisplayName::Width + 1] = "";

bool showColon = true;

enum DisplayState {
  STATE_SCANNING,   // default: always ready to accept a finger and POST /scan
  STATE_SHOW_MATCH,
  STATE_SHOW_AC,
  STATE_SHOW_DENIED,
  STATE_SHOW_SCAN_NAME,
  STATE_ENROLL,
  STATE_MENU_ROLE,
  STATE_MENU_ADMIN_PIN,
  STATE_MENU_FACULTY_ID,
  STATE_MENU_FACULTY_PIN,
  STATE_MENU_ADMIN,
  STATE_MENU_FACULTY,
  STATE_MENU_FACULTY_SCHEDULE,
  STATE_MENU_FACULTY_AC,
  STATE_MENU_ROOMS,
  STATE_MENU_PAIR_CODE,
  STATE_MENU_UNPAIR_CONFIRM,
  STATE_MENU_NOTICE
};

DisplayState currentDisplayState = STATE_SCANNING;
DisplayState pendingScanResultState = STATE_SCANNING;
ScanResultNotice::Sequence pendingScanNotice{};
unsigned long stateTimer = 0;
bool lastScanWasStart = false;  // true = started, false = ended (for AC overlay)

// One scan attempt per finger-down session (avoids spam while finger stays down).
bool fingerDownHandled = false;
bool waitForEnrollmentFingerRemoval = false;
unsigned long lastInventoryAttempt = 0;
const unsigned long INVENTORY_INTERVAL_MS = 60000;

unsigned long lastDHTReadTime = 0;
const unsigned long dhtInterval = 3000;
unsigned long lastTimeDisplayUpdate = 0;
const unsigned long timeDisplayInterval = 1000;
unsigned long lastWiFiRetryTime = 0;
const unsigned long wifiRetryInterval = 180000;
bool wifiWasConnected = false;
bool statusRestoreNeeded = false;
bool serverOnline = false;
unsigned long lastHealthCheckTime = 0;
const unsigned long healthCheckInterval = 30000;  // GET /health every 30s
unsigned long lastHealthLogTime = 0;
unsigned long lastDeviceStatusPollTime = 0;
unsigned long deviceStatusPollingInterval = 30000;
int targetTemperature = 22;
bool attendanceScanEnabled = true;
bool fingerprintEnrollmentEnabled = true;
String deviceDisplayName = "";
String assignedRoomName = "";
bool roomStatusReceived = false;

struct BackgroundRequest {
  BackgroundPoll::Kind kind;
  uint32_t connectionGeneration;
  uint32_t stateGeneration;
  uint32_t pairingGeneration;
  char device[sizeof(deviceId)];
};

struct BackgroundResult {
  BackgroundRequest request;
  String body;
  int code;
  bool received;
  bool legacy;
  bool restore;
};

QueueHandle_t backgroundRequests = nullptr;
QueueHandle_t backgroundResults = nullptr;
TaskHandle_t backgroundTask = nullptr;
bool backgroundBusy = false;
uint32_t connectionGeneration = 0;
uint32_t healthGeneration = 0;
uint32_t statusGeneration = 0;
uint32_t pairingGeneration = 0;
BackgroundResult backgroundResult;

bool isDevicePaired() {
  return deviceId[0] != '\0';
}

struct EnrollmentJournalStorage {
  EnrollmentJournal::ReadStatus read(EnrollmentJournal::Record& record) {
    if (!devicePrefs.begin("smartclass", false)) return EnrollmentJournal::ReadStatus::Error;
    if (!devicePrefs.isKey("enrollJournal")) {
      devicePrefs.end();
      return EnrollmentJournal::ReadStatus::Empty;
    }
    uint8_t bytes[EnrollmentJournal::AttemptRecordSize];
    size_t length = devicePrefs.getBytesLength("enrollJournal");
    bool ok = (length == EnrollmentJournal::RecordSize || length == EnrollmentJournal::AttemptRecordSize) &&
      devicePrefs.getBytes("enrollJournal", bytes, length) == length;
    devicePrefs.end();
    return ok && (length == EnrollmentJournal::RecordSize
                    ? EnrollmentJournal::decode(bytes, record) : EnrollmentJournal::decodeAttempt(bytes, record))
      ? EnrollmentJournal::ReadStatus::Valid : EnrollmentJournal::ReadStatus::Error;
  }

  bool write(const EnrollmentJournal::Record& record) {
    if (!EnrollmentJournal::valid(record)) return false;
    uint8_t bytes[EnrollmentJournal::AttemptRecordSize];
    uint8_t verified[EnrollmentJournal::AttemptRecordSize];
    size_t length = record.kind == EnrollmentJournal::Kind::ReplacementAttempt
      ? EnrollmentJournal::AttemptRecordSize : EnrollmentJournal::RecordSize;
    if (record.kind == EnrollmentJournal::Kind::ReplacementAttempt) EnrollmentJournal::encodeAttempt(record, bytes);
    else EnrollmentJournal::encode(record, bytes);
    if (!devicePrefs.begin("smartclass", false)) return false;
    bool ok = devicePrefs.putBytes("enrollJournal", bytes, length) == length &&
      devicePrefs.getBytesLength("enrollJournal") == length &&
      devicePrefs.getBytes("enrollJournal", verified, length) == length &&
      memcmp(bytes, verified, length) == 0;
    devicePrefs.end();
    return ok;
  }

  bool clear() {
    if (!devicePrefs.begin("smartclass", false)) return false;
    bool ok = !devicePrefs.isKey("enrollJournal") ||
      (devicePrefs.remove("enrollJournal") && !devicePrefs.isKey("enrollJournal"));
    devicePrefs.end();
    return ok;
  }
};

bool enrollmentJournalIsClear() {
  EnrollmentJournalStorage storage;
  EnrollmentJournal::Record record{};
  return storage.read(record) == EnrollmentJournal::ReadStatus::Empty;
}

void loadDeviceId() {
  devicePrefs.begin("smartclass", true);
  String stored = devicePrefs.getString("deviceId", "");
  devicePrefs.end();
  strncpy(deviceId, stored.c_str(), sizeof(deviceId) - 1);
  deviceId[sizeof(deviceId) - 1] = '\0';
}

bool saveDeviceId(const String& id) {
  if (!enrollmentJournalIsClear()) {
    Serial.println("Cannot change pairing: enrollment journal is pending or unavailable.");
    return false;
  }
  if (!devicePrefs.begin("smartclass", false)) {
    return false;
  }
  if (devicePrefs.isKey("enrollKey") && devicePrefs.getString("deviceId", "") != id) {
    devicePrefs.end();
    Serial.println("Cannot change pairing while an enrollment key is provisioned.");
    return false;
  }
  size_t written = devicePrefs.putString("deviceId", id);
  devicePrefs.end();
  if (written != id.length()) {
    return false;
  }
  strncpy(deviceId, id.c_str(), sizeof(deviceId) - 1);
  deviceId[sizeof(deviceId) - 1] = '\0';
  ++pairingGeneration;
  return true;
}

bool isDeviceUnassigned() {
  return roomStatusReceived && assignedRoomName.length() == 0;
}
// Non-blocking USB Serial command line (avoid Serial.readStringUntil stalls).
char serialLineBuf[48];
uint8_t serialLineLen = 0;
bool provisioningKey = false;
bool provisioningSerialLocked = false;
uint8_t provisioningBuffer[64];
size_t provisioningLength = 0;
unsigned long provisioningStarted = 0;

// Keypad menu entry (numeric).
const uint8_t ADMIN_PIN_LENGTH = 6;
const uint8_t MAX_ROOM_OPTIONS = 12;
// Admin PIN is held in RAM only while the admin menu is open.
char adminPinSession[7] = "";
long roomOptionIds[MAX_ROOM_OPTIONS];
char roomOptionLabels[MAX_ROOM_OPTIONS + 1][15];
uint8_t roomOptionCount = 0;
bool roomOptionHasCurrent = false;
const uint8_t MENU_ENTRY_MAX = 11;
const uint8_t MENU_SHORT_ENTRY_MAX = 6;
const unsigned long MENU_IDLE_MS = 30000;
const unsigned long FACULTY_OTP_IDLE_MS = 180000;
const unsigned long MENU_NOTICE_MS = 1400;
const unsigned long ENROLLMENT_NOTICE_MS = 6000;
const unsigned int MENU_BACK_HOLD_MS = 1000;
char entryBuf[MENU_ENTRY_MAX + 1];
uint8_t entryLen = 0;
char facultyIdBuf[MENU_ENTRY_MAX + 1];
char facultySession[65] = "";
FacultyMenu::Schedule facultySchedule{};
FacultyMenu::AcInfo facultyAcInfo{};
enum class FacultyMenuRequestKind : uint8_t { Login, Schedule, Ac, Target };
FacultyMenu::TargetResult facultyTargetResult = FacultyMenu::TargetResult::Denied;
bool facultyScheduleDetails = false;
uint8_t menuIndex = 0;
unsigned long menuActivityAt = 0;
unsigned long menuNoticeDuration = MENU_NOTICE_MS;
DisplayState menuReturnState = STATE_SCANNING;
bool menuNoticeClearEntry = false;
bool menuNoticeClearFaculty = false;
bool pickerZeroPending = false;
DisplayState pickerZeroState = STATE_SCANNING;
bool gsmResponding = false;
bool gsmSimReady = false;
bool gsmNetworkRegistered = false;

String extractJsonString(const String& body, const char* key);
bool extractJsonBool(const String& body, const char* key, bool defaultValue);
int extractJsonInt(const String& body, const char* key, int defaultValue);
void setAcState(bool on);
void rememberActiveTeacher(const String& name);
void clearActiveTeacher();
void applyAcRelay();
bool restoreRoomStatusFromServer();
bool applyRoomStatusFromServer(bool announce);
bool applyRoomStatusResponse(bool announce, bool received, int httpCode, const String& responseBody);
void denyMenuAuth(DisplayState retryState);
bool httpGet(const String& path, String& responseBody, int& httpCode);
bool httpPostJson(const String& path, const String& jsonBody, String& responseBody, int& httpCode, bool touchServerOnline = true);
bool checkServerHealth(bool forceLog = false);
bool deleteAllFingerprintsOnSensor();
bool clearRoomSessionOnServer();
void handleSerialCommand(const String& line);
void processSerialCommands();
bool postScan(int templateId, String& responseBody);
void handleScanResult(int templateId, const String& body, int httpCode);
void showDenied(const char* line0, const char* line1);
void showScanResult(const char* line0, const char* line1, DisplayState resultState);
void printSensorStatus();
const char* fingerprintErrorName(uint8_t code);
int getFingerprintID();
void waitForFingerRemoved();
void enterScanningMode();
void pollScanAndLog();
void updateScanningDisplay();
void manageDisplayState(unsigned long currentMillis);
void beginFingerprintUart();
bool isKeypadMenuState(DisplayState state);
void pollKeypad(unsigned long currentMillis);
void openRoleMenu();
void handleMenuKey(char key);
void menuAppendDigit(char key);
void menuBackspace();
void submitAdminPin();
void submitFacultyId();
void submitFacultyPin();
bool requestFacultyOtp(bool issuing, const char* value, FacultyOtp::Result& result);
bool requestFacultyMenu(FacultyMenuRequestKind kind, const char* value, unsigned index, FacultyMenu::Result& result);
void loadFacultySchedule(unsigned index);
void renderFacultySchedule();
void loadFacultyAc();
void renderFacultyAc();
void adjustFacultyAc(int delta);
bool postFacultyOtpJson(const char* path, const String& body, String& response, int& code, uint16_t timeoutMs);
void runBackgroundPoll(void*);
void scheduleBackgroundPoll(BackgroundPoll::Kind kind, bool restore = false);
void consumeBackgroundPoll();
void executeBackgroundPoll(BackgroundResult& result);
void denyMenuAuth(DisplayState retryState);
void showMenuNotice(const char* line0, const char* line1, DisplayState returnState, bool clearEntry, bool clearFaculty, unsigned long duration = MENU_NOTICE_MS);
void closeMenuNotice();
void renderMenu();
void renderEntryLine();
bool isPickerMenu(DisplayState state);
uint8_t menuItemCount(DisplayState state);
const char* menuItemLabel(DisplayState state, uint8_t index);
void renderPickerTitle();
void menuMove(int delta);
void selectHighlightedItem();
void exitPickerMenu();
void formatKeypadEntryLine(char* out, size_t outLen, bool mask);
void printLcdRow(uint8_t row, const char* text);
void clearKeypadEntry();
bool sendGsmCommand(const char* command, const char* expected, uint32_t timeoutMs, String& response);
void runGsmStartupCheck();

bool enrollmentAuthSelfTest() {
  uint8_t key[32], challenge[32];
  for (uint8_t i = 0; i < 32; ++i) {
    key[i] = i;
    challenge[i] = i + 32;
  }
  const uint8_t expected[32] = {
    0xd9, 0x1f, 0xbb, 0x7e, 0x3d, 0xd3, 0xb8, 0x7b, 0x55, 0xf0, 0xab, 0x95, 0xc1, 0x7b, 0x4c, 0xd2,
    0xe1, 0xbb, 0x8e, 0xdc, 0x36, 0x98, 0x7b, 0x1d, 0x5e, 0xe1, 0x7c, 0x4d, 0xf9, 0x49, 0x7a, 0xcd
  };
  const char body[] = "{\"slot\":2}";
  return EnrollmentAuth::verifyTag(key, sizeof(key), false, "1000000001", "claim", challenge, sizeof(challenge),
                                   reinterpret_cast<const uint8_t*>(body), sizeof(body) - 1, expected, sizeof(expected));
}

// Checks the server-generated recovery response vector (see EnrollmentMessageAuthenticationTests).
bool recoveryAuthSelfTest() {
  uint8_t key[32], challenge[32];
  for (uint8_t i = 0; i < 32; ++i) {
    key[i] = i;
    challenge[i] = i + 32;
  }
  const uint8_t expected[32] = {
    0x36, 0x86, 0x71, 0xf5, 0x35, 0x99, 0xcc, 0x5b, 0x5c, 0xc7, 0x8b, 0xd0, 0x8b, 0x35, 0xfd, 0x24,
    0x8f, 0x3d, 0xab, 0x42, 0xa2, 0xe2, 0x91, 0x85, 0x7e, 0x12, 0x57, 0xa9, 0x67, 0x27, 0x9d, 0x2a
  };
  const char body[] = "11111111-2222-3333-4444-555555555555\n66666666-7777-8888-9999-aaaaaaaaaaaa\n2\n"
                      "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\ncompleted\n";
  return EnrollmentAuth::verifyTag(key, sizeof(key), true, "1000000001", "recover", challenge, sizeof(challenge),
                                   reinterpret_cast<const uint8_t*>(body), strlen(body), expected, sizeof(expected));
}

// Reads the enrolled scanner key without writing preferences. Callers zeroize the buffer.
bool loadEnrollKey(uint8_t* key, size_t keyLength) {
  Preferences prefs;
  bool ok = prefs.begin("smartclass", true) &&
            prefs.getBytesLength("enrollKey") == keyLength &&
            prefs.getBytes("enrollKey", key, keyLength) == keyLength;
  prefs.end();
  return ok;
}

// Verifies a server recovery response tag with the enrolled scanner key. A
// true result only proves the server authenticated the body; it is not write permission.
bool verifyRecoveryResponse(const uint8_t* challenge, size_t challengeLength,
                            const EnrollmentRecovery::Expected& expected,
                            const uint8_t* body, size_t bodyLength,
                            const uint8_t* tag, size_t tagLength,
                            EnrollmentRecovery::State& state) {
  uint8_t key[EnrollmentAuth::KeySize] = {};
  bool haveKey = loadEnrollKey(key, sizeof(key));
  bool ok = haveKey && EnrollmentRecovery::verifyResponse(
      expected,
      [&](const uint8_t* b, size_t bl, const uint8_t* t, size_t tl) {
        return EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, "recover",
                                         challenge, challengeLength, b, bl, t, tl);
      },
      body, bodyLength, tag, tagLength, state);
  mbedtls_platform_zeroize(key, sizeof(key));
  return ok;
}

// Fills a fresh client nonce from the ESP32 hardware RNG.
bool fillHardwareRandom(uint8_t* out, size_t length) {
  for (size_t i = 0; i < length; i += 4) {
    uint32_t word = esp_random();
    for (size_t j = 0; j < 4 && i + j < length; ++j) out[i + j] = static_cast<uint8_t>(word >> (8 * j));
  }
  return true;
}

// Asks the server for the signed recovery state of one preparation slot. This
// only uses HTTP and MAC checks and never touches the sensor. A verified
// "unresolved" or "completed" state is not permission to write.
bool requestRecoveryState(const char* preparationId, const char* operationId, uint16_t slot,
                          EnrollmentRecovery::State& state) {
  if (!isDevicePaired()) return false;

  String response;
  int httpCode = -1;
  String challengePayload = String("{\"deviceId\":\"") + deviceId + "\"}";
  if (!httpPostJson("/api/device/enrollment/recovery/challenge", challengePayload, response, httpCode, false) ||
      httpCode != 200) {
    return false;
  }
  String challengeHex = extractJsonString(response, "challenge");
  uint8_t challenge[EnrollmentAuth::ChallengeSize];
  if (!EnrollmentRecovery::decodeHex(challengeHex.c_str(), challengeHex.length(), challenge, sizeof(challenge))) {
    return false;
  }

  uint8_t key[EnrollmentAuth::KeySize] = {};
  bool ok = loadEnrollKey(key, sizeof(key));
  char nonce[EnrollmentRecovery::NonceHexLength + 1];
  char requestBody[160];
  size_t requestLength = 0;
  uint8_t requestTag[EnrollmentAuth::TagSize];
  ok = ok && EnrollmentRecovery::makeClientNonce(fillHardwareRandom, nonce, sizeof(nonce)) &&
       EnrollmentRecovery::buildRequestBody(preparationId, operationId, slot, nonce,
                                            requestBody, sizeof(requestBody), requestLength) &&
       EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, "recover", challenge, sizeof(challenge),
                                  reinterpret_cast<const uint8_t*>(requestBody), requestLength, requestTag);
  mbedtls_platform_zeroize(key, sizeof(key));
  if (!ok) return false;

  char challengeText[EnrollmentAuth::ChallengeSize * 2 + 1];
  char tagText[EnrollmentAuth::TagSize * 2 + 1];
  EnrollmentAuth::toHex(challenge, sizeof(challenge), challengeText);
  EnrollmentAuth::toHex(requestTag, sizeof(requestTag), tagText);
  String recoveryPayload = String("{\"deviceId\":\"") + deviceId +
                           "\",\"preparationId\":\"" + preparationId +
                           "\",\"operationId\":\"" + operationId +
                           "\",\"slot\":" + String(slot) +
                           ",\"challenge\":\"" + challengeText +
                           "\",\"clientNonce\":\"" + nonce +
                           "\",\"tag\":\"" + tagText + "\"}";
  if (!httpPostJson("/api/device/enrollment/recovery", recoveryPayload, response, httpCode, false) ||
      httpCode != 200) {
    return false;
  }

  String escapedBody = extractJsonString(response, "body");
  String responseTagHex = extractJsonString(response, "tag");
  char responseBody[256];
  size_t responseBodyLength = 0;
  uint8_t responseTag[EnrollmentAuth::TagSize];
  if (!EnrollmentRecovery::decodeJsonBody(escapedBody.c_str(), escapedBody.length(),
                                          responseBody, sizeof(responseBody), responseBodyLength) ||
      !EnrollmentRecovery::decodeHex(responseTagHex.c_str(), responseTagHex.length(),
                                     responseTag, sizeof(responseTag))) {
    return false;
  }
  return verifyRecoveryResponse(challenge, sizeof(challenge),
                                EnrollmentRecovery::Expected{preparationId, operationId, slot, nonce},
                                reinterpret_cast<const uint8_t*>(responseBody), responseBodyLength,
                                responseTag, sizeof(responseTag), state);
}

// Asks the server to record a write guard and sign a grant for an empty slot.
// Only call this after every local check has passed: the server guard is created
// by the claim itself, so a blind retry after a timeout would create another guard.
// A true result is the only permission the sensor write path may rely on.
bool requestClaimWrite(const char* preparationId, const char* operationId, uint16_t slot) {
  if (!isDevicePaired()) return false;

  String response;
  int httpCode = -1;
  String challengePayload = String("{\"deviceId\":\"") + deviceId + "\"}";
  if (!httpPostJson("/api/device/enrollment/claim/challenge", challengePayload, response, httpCode, false) ||
      httpCode != 200) {
    return false;
  }
  String challengeHex = extractJsonString(response, "challenge");
  uint8_t challenge[EnrollmentAuth::ChallengeSize];
  if (!EnrollmentRecovery::decodeHex(challengeHex.c_str(), challengeHex.length(), challenge, sizeof(challenge))) {
    return false;
  }

  // Must match the server's claim MAC body exactly: prep\nop\nslot\n{occupied|empty}\n
  char claimBody[160];
  int claimLength = snprintf(claimBody, sizeof(claimBody), "%s\n%s\n%u\nempty\n",
                             preparationId, operationId, static_cast<unsigned>(slot));
  if (claimLength <= 0 || static_cast<size_t>(claimLength) >= sizeof(claimBody)) return false;

  uint8_t key[EnrollmentAuth::KeySize] = {};
  bool ok = loadEnrollKey(key, sizeof(key));
  uint8_t claimTag[EnrollmentAuth::TagSize];
  ok = ok && EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, "claim", challenge, sizeof(challenge),
                                        reinterpret_cast<const uint8_t*>(claimBody), claimLength, claimTag);
  if (!ok) {
    mbedtls_platform_zeroize(key, sizeof(key));
    return false;
  }

  char challengeText[EnrollmentAuth::ChallengeSize * 2 + 1];
  char tagText[EnrollmentAuth::TagSize * 2 + 1];
  EnrollmentAuth::toHex(challenge, sizeof(challenge), challengeText);
  EnrollmentAuth::toHex(claimTag, sizeof(claimTag), tagText);
  String claimPayload = String("{\"deviceId\":\"") + deviceId +
                        "\",\"preparationId\":\"" + preparationId +
                        "\",\"operationId\":\"" + operationId +
                        "\",\"slot\":" + String(slot) +
                        ",\"occupied\":false,\"challenge\":\"" + challengeText +
                        "\",\"tag\":\"" + tagText + "\"}";
  if (!httpPostJson("/api/device/enrollment/claim", claimPayload, response, httpCode, false) ||
      httpCode != 200) {
    mbedtls_platform_zeroize(key, sizeof(key));
    return false;
  }

  String escapedBody = extractJsonString(response, "body");
  String responseTagHex = extractJsonString(response, "tag");
  char responseBody[256];
  size_t responseBodyLength = 0;
  uint8_t responseTag[EnrollmentAuth::TagSize];
  ok = EnrollmentRecovery::decodeJsonBody(escapedBody.c_str(), escapedBody.length(),
                                          responseBody, sizeof(responseBody), responseBodyLength) &&
       EnrollmentRecovery::decodeHex(responseTagHex.c_str(), responseTagHex.length(),
                                     responseTag, sizeof(responseTag));
  char expectedBody[192];
  int expectedLength = snprintf(expectedBody, sizeof(expectedBody), "%s\n%s\n%u\nempty\ngranted\n",
                                preparationId, operationId, static_cast<unsigned>(slot));
  ok = ok && expectedLength > 0 && static_cast<size_t>(expectedLength) < sizeof(expectedBody) &&
       responseBodyLength == static_cast<size_t>(expectedLength) &&
       memcmp(responseBody, expectedBody, responseBodyLength) == 0 &&
       EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, "claim", challenge, sizeof(challenge),
                                 reinterpret_cast<const uint8_t*>(responseBody), responseBodyLength,
                                 responseTag, sizeof(responseTag));
  mbedtls_platform_zeroize(key, sizeof(key));
  return ok;
}

// Finalizes an already confirmed local journal. A lost response is recoverable:
// recovery reports the durable completion, while a fresh finish challenge makes
// an idempotent retry safe if the server response was lost before commit.
bool requestFinishWrite(const char* preparationId, const char* operationId, uint16_t slot) {
  if (!isDevicePaired()) return false;

  String response;
  int httpCode = -1;
  String challengePayload = String("{\"deviceId\":\"") + deviceId + "\"}";
  if (!httpPostJson("/api/device/enrollment/finish/challenge", challengePayload, response, httpCode, false) ||
      httpCode != 200) {
    return false;
  }
  String challengeHex = extractJsonString(response, "challenge");
  uint8_t challenge[EnrollmentAuth::ChallengeSize];
  if (!EnrollmentRecovery::decodeHex(challengeHex.c_str(), challengeHex.length(), challenge, sizeof(challenge)))
    return false;

  char finishBody[160];
  int bodyLength = snprintf(finishBody, sizeof(finishBody), "%s\n%s\n%u\nconfirmed\n",
                            preparationId, operationId, static_cast<unsigned>(slot));
  if (bodyLength <= 0 || static_cast<size_t>(bodyLength) >= sizeof(finishBody)) return false;

  uint8_t key[EnrollmentAuth::KeySize] = {};
  bool ok = loadEnrollKey(key, sizeof(key));
  uint8_t finishTag[EnrollmentAuth::TagSize];
  ok = ok && EnrollmentAuth::computeTag(
    key, sizeof(key), false, deviceId, "finish", challenge, sizeof(challenge),
    reinterpret_cast<const uint8_t*>(finishBody), static_cast<size_t>(bodyLength), finishTag);
  if (!ok) {
    mbedtls_platform_zeroize(key, sizeof(key));
    return false;
  }

  char challengeText[EnrollmentAuth::ChallengeSize * 2 + 1];
  char tagText[EnrollmentAuth::TagSize * 2 + 1];
  EnrollmentAuth::toHex(challenge, sizeof(challenge), challengeText);
  EnrollmentAuth::toHex(finishTag, sizeof(finishTag), tagText);
  String finishPayload = String("{\"deviceId\":\"") + deviceId +
                         "\",\"preparationId\":\"" + preparationId +
                         "\",\"operationId\":\"" + operationId +
                         "\",\"slot\":" + String(slot) +
                         ",\"challenge\":\"" + challengeText +
                         "\",\"tag\":\"" + tagText + "\"}";
  if (!httpPostJson("/api/device/enrollment/finish", finishPayload, response, httpCode, false) ||
      httpCode != 200) {
    mbedtls_platform_zeroize(key, sizeof(key));
    return false;
  }

  String escapedBody = extractJsonString(response, "body");
  String responseTagHex = extractJsonString(response, "tag");
  char responseBody[192];
  size_t responseBodyLength = 0;
  uint8_t responseTag[EnrollmentAuth::TagSize];
  char expectedBody[160];
  int expectedLength = snprintf(expectedBody, sizeof(expectedBody), "%s\n%s\n%u\ncompleted\n",
                                preparationId, operationId, static_cast<unsigned>(slot));
  ok = EnrollmentRecovery::decodeJsonBody(escapedBody.c_str(), escapedBody.length(),
                                          responseBody, sizeof(responseBody), responseBodyLength) &&
       EnrollmentRecovery::decodeHex(responseTagHex.c_str(), responseTagHex.length(),
                                     responseTag, sizeof(responseTag)) &&
       expectedLength > 0 && static_cast<size_t>(expectedLength) < sizeof(expectedBody) &&
       responseBodyLength == static_cast<size_t>(expectedLength) &&
       memcmp(responseBody, expectedBody, responseBodyLength) == 0 &&
       EnrollmentAuth::verifyTag(
         key, sizeof(key), true, deviceId, "finish", challenge, sizeof(challenge),
         reinterpret_cast<const uint8_t*>(responseBody), responseBodyLength,
         responseTag, sizeof(responseTag));
  mbedtls_platform_zeroize(key, sizeof(key));
  return ok;
}

#if ENROLLMENT_WRITES_ENABLED
bool parseEnrollmentId(const String& text, uint8_t output[EnrollmentJournal::IdSize]) {
  if (text.length() != 36) return false;
  size_t byteIndex = 0;
  int highNibble = -1;
  for (size_t i = 0; i < text.length(); ++i) {
    if (i == 8 || i == 13 || i == 18 || i == 23) {
      if (text[i] != '-') return false;
      continue;
    }
    char ch = text[i];
    uint8_t digit;
    if (ch >= '0' && ch <= '9') digit = static_cast<uint8_t>(ch - '0');
    else if (ch >= 'a' && ch <= 'f') digit = static_cast<uint8_t>(ch - 'a' + 10);
    else return false;
    if (highNibble < 0) highNibble = digit;
    else {
      if (byteIndex >= EnrollmentJournal::IdSize) return false;
      output[byteIndex++] = static_cast<uint8_t>((highNibble << 4) | digit);
      highNibble = -1;
    }
  }
  return byteIndex == EnrollmentJournal::IdSize && highNibble < 0 &&
         EnrollmentJournal::validId(output);
}

bool requestEnrollmentOperation(EnrollmentJournal::Record& operation, int& httpCode) {
  httpCode = -1;
  if (!isDevicePaired()) return false;
  String response;
  String payload = String("{\"deviceId\":\"") + deviceId + "\"}";
  if (!httpPostJson("/api/device/enrollment/operation", payload, response, httpCode, false) ||
      httpCode != 200) {
    if (httpCode != 404)
      Serial.printf("Enrollment operation request failed: HTTP %d.\n", httpCode);
    return false;
  }

  String preparationId = extractJsonString(response, "preparationId");
  String operationId = extractJsonString(response, "operationId");
  String slotText = extractJsonString(response, "slot");
  if (!parseEnrollmentId(preparationId, operation.preparation) ||
      !parseEnrollmentId(operationId, operation.operation) ||
      slotText.length() == 0) {
    Serial.println("Enrollment operation response was invalid.");
    return false;
  }
  char* end = nullptr;
  unsigned long slot = strtoul(slotText.c_str(), &end, 10);
  if (end == slotText.c_str() || *end != '\0' || slot == 0 || slot > UINT16_MAX) {
    Serial.println("Enrollment operation slot was invalid.");
    return false;
  }
  operation.slot = static_cast<uint16_t>(slot);
  operation.state = EnrollmentJournal::State::Uncertain;
  operation.kind = EnrollmentJournal::Kind::NewEnrollment;
  return true;
}

struct EnrollmentSerialTransport {
  int available() { return mySerial.available(); }
  int read() { return mySerial.read(); }
  size_t write(uint8_t value) { return mySerial.write(value); }
};

struct EnrollmentClock {
  uint32_t now() { return millis(); }
  void pause(uint32_t duration) { delay(duration); }
};

void reportSensorInventory() {
  if (!isDevicePaired()) return;
  String response;
  int httpCode = -1;
  String challengePayload = String("{\"deviceId\":\"") + deviceId + "\"}";
  if (!httpPostJson("/api/device/enrollment/inventory/challenge", challengePayload, response, httpCode, false) ||
      httpCode != 200) {
    Serial.printf("Inventory challenge failed: HTTP %d.\n", httpCode);
    return;
  }
  String challengeHex = extractJsonString(response, "challenge");
  uint8_t challenge[EnrollmentAuth::ChallengeSize];
  if (!EnrollmentRecovery::decodeHex(challengeHex.c_str(), challengeHex.length(), challenge, sizeof(challenge))) {
    Serial.println("Inventory challenge was invalid.");
    return;
  }
  EnrollmentSerialTransport transport;
  EnrollmentClock clock;
  EnrollmentSensor::CheckedSensor<EnrollmentSerialTransport, EnrollmentClock> sensor(transport, clock);
  uint16_t capacity = 0;
  uint8_t bitmap[EnrollmentInventory::MaxBitmapBytes] = {};
  size_t length = 0;
  uint8_t status = EnrollmentInventory::read(sensor, capacity, bitmap, length);
  if (status != EnrollmentSensor::Ok) {
    Serial.printf("Sensor inventory failed: %u.\n", status);
    return;
  }
  char bitmapText[EnrollmentInventory::MaxBitmapBytes * 2 + 1];
  EnrollmentAuth::toHex(bitmap, length, bitmapText);
  String body = String(capacity) + "\n" + bitmapText + "\n";
  uint8_t key[EnrollmentAuth::KeySize] = {};
  uint8_t tag[EnrollmentAuth::TagSize];
  bool ok = loadEnrollKey(key, sizeof(key)) &&
            EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, "inventory", challenge, sizeof(challenge),
                                       reinterpret_cast<const uint8_t*>(body.c_str()), body.length(), tag);
  mbedtls_platform_zeroize(key, sizeof(key));
  if (!ok) {
    Serial.println("Inventory authentication failed locally.");
    return;
  }
  char tagText[EnrollmentAuth::TagSize * 2 + 1];
  EnrollmentAuth::toHex(tag, sizeof(tag), tagText);
  String payload = String("{\"deviceId\":\"") + deviceId + "\",\"capacity\":" + String(capacity) +
                   ",\"bitmap\":\"" + bitmapText + "\",\"challenge\":\"" + challengeHex +
                   "\",\"tag\":\"" + tagText + "\"}";
  if (!httpPostJson("/api/device/enrollment/inventory", payload, response, httpCode, false) || httpCode != 200)
    Serial.printf("Inventory report failed: HTTP %d.\n", httpCode);
  else
    Serial.printf("Sensor inventory reported: capacity %u.\n", static_cast<unsigned>(capacity));
}

struct EnrollmentCaptureDisplay {
  void show(EnrollmentCapture::Stage stage) {
    lcd.clear();
    lcd.setCursor(0, 0);
    switch (stage) {
      case EnrollmentCapture::Stage::FirstFinger: lcd.print("Place finger"); break;
      case EnrollmentCapture::Stage::RemoveFinger: lcd.print("Remove finger"); break;
      case EnrollmentCapture::Stage::SecondFinger: lcd.print("Place same finger"); break;
    }
    lcd.setCursor(0, 1);
    lcd.print("Enrollment");
  }
};

struct EnrollmentSensorAdapter {
  EnrollmentSensor::CheckedSensor<EnrollmentSerialTransport, EnrollmentClock>& checked;
  EnrollmentClock& clock;
  EnrollmentCaptureDisplay& display;

  uint8_t inspectSlot(uint16_t slot, bool& occupied) {
    return checked.inspectSlot(slot, occupied);
  }

  bool prepareModel() {
    EnrollmentCapture::Result result = EnrollmentCapture::captureModel(checked, clock, display);
    if (result.status == EnrollmentCapture::Status::ModelReady) return true;
    Serial.printf("Enrollment capture failed: stage=%u sensor=%u\n",
                  static_cast<unsigned>(result.stage), result.sensorCode);
    lcd.clear();
    lcd.setCursor(0, 0);
    lcd.print(result.status == EnrollmentCapture::Status::TimedOut ? "Capture timed out" : "Sensor error");
    return false;
  }

  uint8_t storeModel(uint16_t slot) {
    uint8_t result = checked.storeModel(slot);
    if (result != EnrollmentSensor::Ok)
      Serial.printf("Enrollment store acknowledgement failed: %u\n", result);
    return result;
  }
};

bool formatEnrollmentId(const uint8_t id[EnrollmentJournal::IdSize], char output[37]) {
  return EnrollmentJournal::formatId(id, output);
}

struct ReplacementIntentAdapter {
  uint8_t key[EnrollmentAuth::KeySize] = {};
  ~ReplacementIntentAdapter() { mbedtls_platform_zeroize(key, sizeof(key)); }

  bool getChallenge(uint8_t* challenge) {
    if (!isDevicePaired() || !loadEnrollKey(key, sizeof(key))) return false;
    String response;
    int httpCode = -1;
    String payload = String("{\"deviceId\":\"") + deviceId + "\"}";
    if (!httpPostJson("/api/device/enrollment/replacement-intent/challenge", payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String text = extractJsonString(response, "challenge");
    return EnrollmentRecovery::decodeHex(text.c_str(), text.length(), challenge, EnrollmentAuth::ChallengeSize);
  }

  bool makeNonce(char* nonce) {
    return EnrollmentRecovery::makeClientNonce(fillHardwareRandom, nonce, EnrollmentRecovery::NonceHexLength + 1);
  }

  bool sign(const uint8_t* challenge, const uint8_t* body, size_t length, uint8_t* tag) {
    return EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, "replacement_intent",
        challenge, EnrollmentAuth::ChallengeSize, body, length, tag);
  }

  bool exchange(const char* preparation, const char* operation, uint16_t slot, const char* nonce,
                const uint8_t* challenge, const uint8_t* tag, uint8_t* body, size_t bodySize,
                size_t& bodyLength, uint8_t* responseTag) {
    char challengeText[EnrollmentAuth::ChallengeSize * 2 + 1], tagText[EnrollmentAuth::TagSize * 2 + 1];
    EnrollmentAuth::toHex(challenge, EnrollmentAuth::ChallengeSize, challengeText);
    EnrollmentAuth::toHex(tag, EnrollmentAuth::TagSize, tagText);
    String payload = String("{\"deviceId\":\"") + deviceId +
                     "\",\"preparationId\":\"" + preparation + "\",\"operationId\":\"" + operation +
                     "\",\"slot\":" + String(slot) + ",\"challenge\":\"" + challengeText +
                     "\",\"clientNonce\":\"" + nonce + "\",\"tag\":\"" + tagText + "\"}";
    String response;
    int httpCode = -1;
    if (!httpPostJson("/api/device/enrollment/replacement-intent", payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String escapedBody = extractJsonString(response, "body");
    String tagHex = extractJsonString(response, "tag");
    return EnrollmentRecovery::decodeJsonBody(escapedBody.c_str(), escapedBody.length(),
               reinterpret_cast<char*>(body), bodySize, bodyLength) &&
           EnrollmentRecovery::decodeHex(tagHex.c_str(), tagHex.length(), responseTag, EnrollmentAuth::TagSize);
  }

  bool verify(const uint8_t* challenge, const uint8_t* body, size_t bodyLength,
              const uint8_t* tag, size_t tagLength) {
    return EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, "replacement_intent",
        challenge, EnrollmentAuth::ChallengeSize, body, bodyLength, tag, tagLength);
  }
};

struct ReplacementOperationAdapter : ReplacementIntentAdapter {
  bool getChallenge(uint8_t* challenge) {
    if (!isDevicePaired() || !loadEnrollKey(key, sizeof(key))) return false;
    String response;
    int httpCode = -1;
    String payload = String("{\"deviceId\":\"") + deviceId + "\"}";
    if (!httpPostJson("/api/device/enrollment/replacement-operation/challenge", payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String text = extractJsonString(response, "challenge");
    return EnrollmentRecovery::decodeHex(text.c_str(), text.length(), challenge, EnrollmentAuth::ChallengeSize);
  }
  bool sign(const uint8_t* challenge, const uint8_t* body, size_t length, uint8_t* tag) {
    return EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, "replacement_operation",
        challenge, EnrollmentAuth::ChallengeSize, body, length, tag);
  }
  bool exchange(const char* nonce, const uint8_t* challenge, const uint8_t* tag,
                uint8_t* body, size_t bodySize, size_t& bodyLength, uint8_t* responseTag) {
    char challengeText[EnrollmentAuth::ChallengeSize * 2 + 1], tagText[EnrollmentAuth::TagSize * 2 + 1];
    EnrollmentAuth::toHex(challenge, EnrollmentAuth::ChallengeSize, challengeText);
    EnrollmentAuth::toHex(tag, EnrollmentAuth::TagSize, tagText);
    String payload = String("{\"deviceId\":\"") + deviceId + "\",\"challenge\":\"" + challengeText +
        "\",\"clientNonce\":\"" + nonce + "\",\"tag\":\"" + tagText + "\"}";
    String response;
    int httpCode = -1;
    if (!httpPostJson("/api/device/enrollment/replacement-operation", payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String escapedBody = extractJsonString(response, "body");
    String tagHex = extractJsonString(response, "tag");
    return EnrollmentRecovery::decodeJsonBody(escapedBody.c_str(), escapedBody.length(),
               reinterpret_cast<char*>(body), bodySize, bodyLength) &&
           EnrollmentRecovery::decodeHex(tagHex.c_str(), tagHex.length(), responseTag, EnrollmentAuth::TagSize);
  }
  bool verify(const uint8_t* challenge, const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
    return EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, "replacement_operation",
        challenge, EnrollmentAuth::ChallengeSize, body, length, tag, tagLength);
  }
};

bool discoverReplacementOperation(EnrollmentJournal::Record& operation) {
  ReplacementOperationAdapter adapter;
  return ReplacementOperation::fetch(adapter, operation);
}

struct ReplacementAttemptAdapter : ReplacementIntentAdapter {
  const char* challengePath;
  const char* path;
  const char* purpose;

  ReplacementAttemptAdapter(const char* challengePath, const char* path, const char* purpose)
    : challengePath(challengePath), path(path), purpose(purpose) {}

  bool getChallenge(uint8_t* challenge) {
    if (!isDevicePaired() || !loadEnrollKey(key, sizeof(key))) return false;
    String response;
    int httpCode = -1;
    String payload = String("{\"deviceId\":\"") + deviceId + "\"}";
    if (!httpPostJson(challengePath, payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String text = extractJsonString(response, "challenge");
    return EnrollmentRecovery::decodeHex(text.c_str(), text.length(), challenge, EnrollmentAuth::ChallengeSize);
  }

  bool sign(const uint8_t* challenge, const uint8_t* body, size_t length, uint8_t* tag) {
    return EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, purpose,
        challenge, EnrollmentAuth::ChallengeSize, body, length, tag);
  }

  bool exchange(const char* preparation, const char* operation, const char* recovery, uint16_t slot,
                const char* nonce, const uint8_t* challenge, const uint8_t* tag, uint8_t* body,
                size_t bodySize, size_t& bodyLength, uint8_t* responseTag) {
    char challengeText[EnrollmentAuth::ChallengeSize * 2 + 1], tagText[EnrollmentAuth::TagSize * 2 + 1];
    EnrollmentAuth::toHex(challenge, EnrollmentAuth::ChallengeSize, challengeText);
    EnrollmentAuth::toHex(tag, EnrollmentAuth::TagSize, tagText);
    String payload = String("{\"deviceId\":\"") + deviceId +
                     "\",\"preparationId\":\"" + preparation + "\",\"operationId\":\"" + operation +
                     "\",\"recoveryOperationId\":\"" + recovery + "\",\"slot\":" + String(slot) +
                     ",\"challenge\":\"" + challengeText + "\",\"clientNonce\":\"" + nonce +
                     "\",\"tag\":\"" + tagText + "\"}";
    String response;
    int httpCode = -1;
    if (!httpPostJson(path, payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String escapedBody = extractJsonString(response, "body");
    String tagHex = extractJsonString(response, "tag");
    return EnrollmentRecovery::decodeJsonBody(escapedBody.c_str(), escapedBody.length(),
               reinterpret_cast<char*>(body), bodySize, bodyLength) &&
           EnrollmentRecovery::decodeHex(tagHex.c_str(), tagHex.length(), responseTag, EnrollmentAuth::TagSize);
  }

  bool verify(const uint8_t* challenge, const uint8_t* body, size_t bodyLength,
              const uint8_t* tag, size_t tagLength) {
    return EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, purpose,
        challenge, EnrollmentAuth::ChallengeSize, body, bodyLength, tag, tagLength);
  }
};

struct ReplacementAttemptStatusAdapter : ReplacementAttemptAdapter {
  ReplacementAttemptStatusAdapter() : ReplacementAttemptAdapter(
      "/api/device/enrollment/replacement-attempt-status/challenge",
      "/api/device/enrollment/replacement-attempt-status", "replacement_attempt_status") {}
};

struct ReplacementAttemptReserveAdapter : ReplacementAttemptAdapter {
  ReplacementAttemptReserveAdapter() : ReplacementAttemptAdapter(
      "/api/device/enrollment/replacement-attempt-reserve/challenge",
      "/api/device/enrollment/replacement-attempt-reserve", "replacement_attempt_reserve") {}
};

struct ReplacementAttemptClaimAdapter : ReplacementAttemptAdapter {
  ReplacementAttemptClaimAdapter() : ReplacementAttemptAdapter(
      "/api/device/enrollment/replacement-attempt-claim/challenge",
      "/api/device/enrollment/replacement-attempt-claim", "replacement_attempt_claim") {}

  bool claimWrite(const EnrollmentJournal::Record& prepared) {
    return ReplacementAttemptClaim::claim(prepared, *this);
  }
};

struct ReplacementAttemptFinishAdapter : ReplacementAttemptAdapter {
  ReplacementAttemptFinishAdapter() : ReplacementAttemptAdapter(
      "/api/device/enrollment/replacement-attempt-finish/challenge",
      "/api/device/enrollment/replacement-attempt-finish", "replacement_attempt_finish") {}
};

struct ReplacementAttemptFenceAdapter : ReplacementAttemptAdapter {
  ReplacementAttemptFenceAdapter() : ReplacementAttemptAdapter(
      "/api/device/enrollment/replacement-attempt-fence/challenge",
      "/api/device/enrollment/replacement-attempt-fence", "replacement_attempt_fence") {}
};

ReplacementAttemptFence::Result fenceReplacementAttempt(const EnrollmentJournal::Record& attempt) {
  EnrollmentJournalStorage storage;
  ReplacementAttemptFenceAdapter adapter;
  return ReplacementAttemptFence::attest(storage, adapter, attempt);
}

bool claimReplacementAttempt(const EnrollmentJournal::Record& attempt) {
  ReplacementAttemptClaimAdapter adapter;
  return ReplacementAttemptClaim::claim(attempt, adapter);
}

ReplacementAttemptWrite::Result runReplacementAttemptWriteOnce(EnrollmentSensorAdapter& sensor,
                                                                const EnrollmentJournal::Record& prepared) {
  EnrollmentJournalStorage storage;
  ReplacementAttemptClaimAdapter grant;
  return ReplacementAttemptWrite::writeOnce(storage, sensor, grant, prepared);
}

ReplacementAttemptReserve::Result reserveReplacementAttempt(const EnrollmentJournal::Record& fenced,
                                                             const uint8_t* recoveryId) {
  EnrollmentJournalStorage storage;
  ReplacementAttemptReserveAdapter adapter;
  return ReplacementAttemptReserve::reserve(storage, adapter, fenced, recoveryId);
}

struct ReplacementFinishAdapter : ReplacementIntentAdapter {
  bool getChallenge(uint8_t* challenge) {
    if (!isDevicePaired() || !loadEnrollKey(key, sizeof(key))) return false;
    String response;
    int httpCode = -1;
    String payload = String("{\"deviceId\":\"") + deviceId + "\"}";
    if (!httpPostJson("/api/device/enrollment/replacement-finish/challenge", payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String text = extractJsonString(response, "challenge");
    return EnrollmentRecovery::decodeHex(text.c_str(), text.length(), challenge, EnrollmentAuth::ChallengeSize);
  }

  bool sign(const uint8_t* challenge, const uint8_t* body, size_t length, uint8_t* tag) {
    return EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, "replacement_finish",
        challenge, EnrollmentAuth::ChallengeSize, body, length, tag);
  }

  bool exchange(const char* preparation, const char* operation, uint16_t slot, const char* nonce,
                const uint8_t* challenge, const uint8_t* tag, uint8_t* body, size_t bodySize,
                size_t& bodyLength, uint8_t* responseTag) {
    char challengeText[EnrollmentAuth::ChallengeSize * 2 + 1], tagText[EnrollmentAuth::TagSize * 2 + 1];
    EnrollmentAuth::toHex(challenge, EnrollmentAuth::ChallengeSize, challengeText);
    EnrollmentAuth::toHex(tag, EnrollmentAuth::TagSize, tagText);
    String payload = String("{\"deviceId\":\"") + deviceId +
                     "\",\"preparationId\":\"" + preparation + "\",\"operationId\":\"" + operation +
                     "\",\"slot\":" + String(slot) + ",\"challenge\":\"" + challengeText +
                     "\",\"clientNonce\":\"" + nonce + "\",\"tag\":\"" + tagText + "\"}";
    String response;
    int httpCode = -1;
    if (!httpPostJson("/api/device/enrollment/replacement-finish", payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String escapedBody = extractJsonString(response, "body");
    String tagHex = extractJsonString(response, "tag");
    return EnrollmentRecovery::decodeJsonBody(escapedBody.c_str(), escapedBody.length(),
               reinterpret_cast<char*>(body), bodySize, bodyLength) &&
           EnrollmentRecovery::decodeHex(tagHex.c_str(), tagHex.length(), responseTag, EnrollmentAuth::TagSize);
  }

  bool verify(const uint8_t* challenge, const uint8_t* body, size_t bodyLength,
              const uint8_t* tag, size_t tagLength) {
    return EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, "replacement_finish",
        challenge, EnrollmentAuth::ChallengeSize, body, bodyLength, tag, tagLength);
  }
};

struct ReplacementGuardAdapter : ReplacementIntentAdapter {
  const char* challengePath;
  const char* path;
  const char* purpose;

  ReplacementGuardAdapter(const char* challengePath, const char* path, const char* purpose)
    : challengePath(challengePath), path(path), purpose(purpose) {}

  bool getChallenge(uint8_t* challenge) {
    if (!isDevicePaired() || !loadEnrollKey(key, sizeof(key))) return false;
    String response;
    int httpCode = -1;
    String payload = String("{\"deviceId\":\"") + deviceId + "\"}";
    if (!httpPostJson(challengePath, payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String text = extractJsonString(response, "challenge");
    return EnrollmentRecovery::decodeHex(text.c_str(), text.length(), challenge, EnrollmentAuth::ChallengeSize);
  }

  bool sign(const uint8_t* challenge, const uint8_t* body, size_t length, uint8_t* tag) {
    return EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, purpose,
        challenge, EnrollmentAuth::ChallengeSize, body, length, tag);
  }

  bool exchange(const char* preparation, const char* operation, uint16_t slot, const char* nonce,
                const uint8_t* challenge, const uint8_t* tag, uint8_t* body, size_t bodySize,
                size_t& bodyLength, uint8_t* responseTag) {
    char challengeText[EnrollmentAuth::ChallengeSize * 2 + 1], tagText[EnrollmentAuth::TagSize * 2 + 1];
    EnrollmentAuth::toHex(challenge, EnrollmentAuth::ChallengeSize, challengeText);
    EnrollmentAuth::toHex(tag, EnrollmentAuth::TagSize, tagText);
    String payload = String("{\"deviceId\":\"") + deviceId +
                     "\",\"preparationId\":\"" + preparation + "\",\"operationId\":\"" + operation +
                     "\",\"slot\":" + String(slot) + ",\"challenge\":\"" + challengeText +
                     "\",\"clientNonce\":\"" + nonce + "\",\"tag\":\"" + tagText + "\"}";
    String response;
    int httpCode = -1;
    if (!httpPostJson(path, payload, response, httpCode, false) ||
        httpCode != 200) return false;
    String escapedBody = extractJsonString(response, "body");
    String tagHex = extractJsonString(response, "tag");
    return EnrollmentRecovery::decodeJsonBody(escapedBody.c_str(), escapedBody.length(),
               reinterpret_cast<char*>(body), bodySize, bodyLength) &&
           EnrollmentRecovery::decodeHex(tagHex.c_str(), tagHex.length(), responseTag, EnrollmentAuth::TagSize);
  }

  bool verify(const uint8_t* challenge, const uint8_t* body, size_t bodyLength,
              const uint8_t* tag, size_t tagLength) {
    return EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, purpose,
        challenge, EnrollmentAuth::ChallengeSize, body, bodyLength, tag, tagLength);
  }
};

struct ReplacementGuardStatusAdapter : ReplacementGuardAdapter {
  ReplacementGuardStatusAdapter() : ReplacementGuardAdapter(
      "/api/device/enrollment/replacement-guard-status/challenge",
      "/api/device/enrollment/replacement-guard-status", "replacement_guard_status") {}
};

struct ReplacementGuardReserveAdapter : ReplacementGuardAdapter {
  ReplacementGuardReserveAdapter() : ReplacementGuardAdapter(
      "/api/device/enrollment/replacement-guard-reserve/challenge",
      "/api/device/enrollment/replacement-guard-reserve", "replacement_guard_reserve") {}
};

struct ReplacementWriteClaimAdapter : ReplacementGuardAdapter {
  ReplacementWriteClaimAdapter() : ReplacementGuardAdapter(
      "/api/device/enrollment/replacement-write-claim/challenge",
      "/api/device/enrollment/replacement-write-claim", "replacement_write_claim") {}

  bool claimWrite(const EnrollmentJournal::Record& prepared) {
    return ReplacementWriteClaim::claim(prepared, *this);
  }
};

struct ReplacementWriteFenceAdapter : ReplacementGuardAdapter {
  ReplacementWriteFenceAdapter() : ReplacementGuardAdapter(
      "/api/device/enrollment/replacement-write-fence/challenge",
      "/api/device/enrollment/replacement-write-fence", "replacement_write_fence") {}
};

ReplacementWriteFence::Result fenceReplacementWrite() {
  EnrollmentJournalStorage storage;
  ReplacementWriteFenceAdapter adapter;
  return ReplacementWriteFence::fence(storage, adapter);
}

ReplacementGuardReserve::Result reserveReplacementGuard(const EnrollmentJournal::Record& pending) {
  EnrollmentJournalStorage storage;
  ReplacementGuardReserveAdapter adapter;
  return ReplacementGuardReserve::reserve(storage, adapter, pending);
}

ReplacementWrite::Result runReplacementWriteOnce(EnrollmentSensorAdapter& sensor,
                                                 const EnrollmentJournal::Record& prepared) {
  EnrollmentJournalStorage storage;
  ReplacementWriteClaimAdapter grant;
  return ReplacementWrite::writeOnce(storage, sensor, grant, prepared);
}

struct EnrollmentServerAdapter {
  bool fetchRecoveryState(const EnrollmentJournal::Record& record, EnrollmentRecovery::State& state) {
    char preparation[37], operation[37];
    if (!formatEnrollmentId(record.preparation, preparation) ||
        !formatEnrollmentId(record.operation, operation)) {
      Serial.println("Enrollment recovery refused: invalid operation identifier.");
      return false;
    }
    bool ok = requestRecoveryState(preparation, operation, record.slot, state);
    if (!ok) Serial.println("Enrollment recovery state unavailable or unauthenticated.");
    return ok;
  }

  bool claimWrite(const uint8_t* preparationId, const uint8_t* operationId, uint16_t slot) {
    char preparation[37], operation[37];
    if (!formatEnrollmentId(preparationId, preparation) ||
        !formatEnrollmentId(operationId, operation)) {
      Serial.println("Enrollment claim refused: invalid operation identifier.");
      return false;
    }
    bool ok = requestClaimWrite(preparation, operation, slot);
    if (!ok) Serial.println("Enrollment write claim unavailable or unauthenticated.");
    return ok;
  }

  bool finishWrite(const uint8_t* preparationId, const uint8_t* operationId, uint16_t slot) {
    char preparation[37], operation[37];
    if (!formatEnrollmentId(preparationId, preparation) ||
        !formatEnrollmentId(operationId, operation)) {
      Serial.println("Enrollment finish refused: invalid operation identifier.");
      return false;
    }
    bool ok = requestFinishWrite(preparation, operation, slot);
    if (!ok) Serial.println("Enrollment completion pending: server response unavailable or unauthenticated.");
    return ok;
  }
};

EnrollmentStep::Result runEnrollmentOnce(const EnrollmentJournal::Record& intended) {
  EnrollmentSerialTransport transport;
  EnrollmentClock clock;
  EnrollmentCaptureDisplay display;
  EnrollmentSensor::CheckedSensor<EnrollmentSerialTransport, EnrollmentClock> checked(transport, clock);
  EnrollmentSensorAdapter sensor{checked, clock, display};
  EnrollmentJournalStorage storage;
  EnrollmentServerAdapter server;
  return EnrollmentStep::runOnce(storage, sensor, server, intended);
}

#if REPLACEMENT_WRITES_ENABLED
EnrollmentNotice::Message runReplacementOnce() {
  EnrollmentJournal::Record operation{};
  if (!discoverReplacementOperation(operation)) {
    Serial.println("Replacement operation unavailable or unauthenticated.");
    return {"Replace failed", "Check portal"};
  }
  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print("Replace slot ");
  lcd.print(operation.slot);
  lcd.setCursor(0, 1);
  lcd.print("# Yes  * Cancel");
  unsigned long started = millis();
  bool confirmed = false;
  while (millis() - started < 30000UL) {
    char key = keypad.getKey();
    if (key == '#') { confirmed = true; break; }
    if (key == '*') break;
    delay(10);
  }
  if (!confirmed) return {"Replace canceled", "No write done"};
  EnrollmentSerialTransport transport;
  EnrollmentClock clock;
  EnrollmentCaptureDisplay display;
  EnrollmentSensor::CheckedSensor<EnrollmentSerialTransport, EnrollmentClock> checked(transport, clock);
  EnrollmentSensorAdapter sensor{checked, clock, display};
  EnrollmentJournalStorage storage;
  ReplacementGuardReserveAdapter reserve;
  ReplacementWriteClaimAdapter grant;
  ReplacementFinishAdapter finish;
  auto result = ReplacementStep::runOnce(storage, sensor, reserve, grant, finish, operation);
  switch (result) {
    case ReplacementStep::Outcome::Completed: return {"Enroll complete", "Remove finger"};
    case ReplacementStep::Outcome::Refused: return {"Slot not ready", "Contact admin"};
    case ReplacementStep::Outcome::CaptureFailure: return {"Capture failed", "Check scanner"};
    case ReplacementStep::Outcome::Held: return {"Replace held", "Contact admin"};
    case ReplacementStep::Outcome::Uncertain: return {"Write uncertain", "Contact admin"};
    case ReplacementStep::Outcome::CompletionPending: return {"Finish pending", "Contact admin"};
  }
  return {"Replace held", "Contact admin"};
}
#endif

EnrollmentNotice::Message runEnrollmentFromServer() {
  EnrollmentJournalStorage storage;
  EnrollmentJournal::Record pending{};
  EnrollmentJournal::ReadStatus journalStatus = storage.read(pending);
  if (journalStatus == EnrollmentJournal::ReadStatus::Error) {
    Serial.println("Enrollment refused: journal is corrupt or unavailable.");
    return {"Journal error", "Contact admin"};
  }
  if (journalStatus == EnrollmentJournal::ReadStatus::Valid) {
    if (pending.kind == EnrollmentJournal::Kind::ReplacementAttempt) {
      if (pending.state == EnrollmentJournal::State::RecoveryFenced) {
        Serial.println("Recovery attempt fenced locally; recovery remains blocked.");
        return {"Recovery held", "Contact admin"};
      }
      if (pending.state == EnrollmentJournal::State::Confirmed) {
        ReplacementAttemptFinishAdapter adapter;
        if (ReplacementAttemptFinish::finish(storage, adapter) == ReplacementAttemptFinish::Result::Completed) {
          Serial.println("Confirmed recovery completed and journal cleared.");
          return {"Enroll complete", "Remove finger"};
        }
        Serial.println("Confirmed recovery completion pending; journal retained or unavailable.");
        return {"Finish pending", "Contact admin"};
      }
      ReplacementAttemptStatusAdapter adapter;
      ReplacementAttemptStatus::State status;
      bool observed = ReplacementAttemptStatus::observe(pending, adapter, status);
      if (!observed)
        Serial.println("Replacement attempt status unavailable or invalid; recovery remains blocked.");
      else if (status == ReplacementAttemptStatus::State::Reserved)
        Serial.println("Replacement attempt reserved; recovery remains blocked.");
      else
        Serial.println("Replacement attempt not reserved; recovery remains blocked.");
      return {"Recovery held", "Contact admin"};
    }
    if (pending.kind == EnrollmentJournal::Kind::Replacement) {
      if (pending.state == EnrollmentJournal::State::ReplacementFenced) {
        Serial.println("Replacement write fenced locally; recovery remains blocked.");
        return {"Recovery held", "Contact admin"};
      }
      if (pending.state == EnrollmentJournal::State::Confirmed) {
        ReplacementFinishAdapter adapter;
        if (ReplacementFinish::finish(storage, adapter) == ReplacementFinish::Result::Completed) {
          Serial.println("Confirmed replacement completed and journal cleared.");
          return {"Enroll complete", "Remove finger"};
        }
        Serial.println("Confirmed replacement completion pending; journal retained or unavailable.");
        return {"Finish pending", "Contact admin"};
      }
      ReplacementGuardStatusAdapter guardAdapter;
      ReplacementGuardStatus::State guardStatus;
      if (!ReplacementGuardStatus::observe(pending, guardAdapter, guardStatus))
        Serial.println("Replacement guard status unavailable or invalid; recovery remains blocked.");
      else if (guardStatus == ReplacementGuardStatus::State::Guarded)
        Serial.println("Replacement guard exists; recovery remains blocked.");
      else if (guardStatus == ReplacementGuardStatus::State::NotGuarded)
        Serial.println("Replacement not guarded at observation; recovery remains blocked.");
      else
        Serial.println("Replacement completion recorded; pending journal remains held.");
      if (pending.state == EnrollmentJournal::State::ReplacementPrepared) {
        Serial.println("Replacement was prepared before any write; journal remains held.");
        return {"Reserve held", "Contact admin"};
      }
      ReplacementIntentAdapter adapter;
      char recoveryOperation[37];
      bool observed = ReplacementIntent::observe(pending, adapter, recoveryOperation);
      Serial.println(observed ? "Replacement intent verified; recovery remains blocked."
                              : "Replacement intent unavailable or invalid; recovery remains blocked.");
      return {"Recovery held", "Contact admin"};
    }
    EnrollmentStep::Result recovery = runEnrollmentOnce(EnrollmentJournal::Record{});
    Serial.printf("Enrollment recovery outcome=%u decision=%u\n",
                  static_cast<unsigned>(recovery.outcome), static_cast<unsigned>(recovery.decision));
    return EnrollmentNotice::forResult(recovery);
  }

  EnrollmentJournal::Record intended{};
  int httpCode;
  if (!requestEnrollmentOperation(intended, httpCode)) {
    return httpCode == 404
      ? EnrollmentNotice::Message{"Not eligible", "Check portal"}
      : EnrollmentNotice::Message{"Request failed", "Check server"};
  }
  EnrollmentStep::Result result = runEnrollmentOnce(intended);
  Serial.printf("Enrollment pass outcome=%u decision=%u\n",
                static_cast<unsigned>(result.outcome), static_cast<unsigned>(result.decision));
  return EnrollmentNotice::forResult(result);
}
#endif

void runBuzzerMelody(void*) {
  for (;;) {
    ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
    const uint32_t startedAt = millis();
    for (;;) {
      BuzzerMelody::Position position = BuzzerMelody::positionAt(startedAt, millis());
      if (!position.waitMs) break;
      if (position.toneDurationMs)
        tone(BUZZER_PIN, position.frequency, position.toneDurationMs);
      TickType_t waitTicks = pdMS_TO_TICKS(position.waitMs);
      vTaskDelay(waitTicks ? waitTicks : 1);
    }
  }
}

void executeBackgroundPoll(BackgroundResult& result) {
  result.body = "";
  result.code = -1;
  result.received = false;
  result.legacy = false;
  if (result.request.kind == BackgroundPoll::Kind::Health) {
    result.received = httpGet("/health", result.body, result.code);
  } else {
    String payload = String("{\"deviceId\":\"") + result.request.device + "\"}";
    if (result.request.kind == BackgroundPoll::Kind::Status) {
      result.received = httpPostJson("/api/device/status", payload, result.body, result.code, false);
    } else {
      result.received = postFacultyOtpJson("/api/device/buzzer/v2", payload, result.body, result.code, 4000);
      result.legacy = result.received && result.code == 404;
      if (result.legacy)
        result.received = postFacultyOtpJson("/api/device/buzzer", payload, result.body, result.code, 4000);
    }
  }
}

void runBackgroundPoll(void*) {
  BackgroundResult* result = nullptr;
  for (;;) {
    if (xQueueReceive(backgroundRequests, &result, portMAX_DELAY) == pdTRUE) {
      executeBackgroundPoll(*result);
      xQueueSend(backgroundResults, &result, portMAX_DELAY);
    }
  }
}

void scheduleBackgroundPoll(BackgroundPoll::Kind kind, bool restore) {
  if (backgroundBusy) return;
  backgroundResult.request.kind = kind;
  backgroundResult.request.connectionGeneration = connectionGeneration;
  backgroundResult.request.stateGeneration = kind == BackgroundPoll::Kind::Health ? healthGeneration : statusGeneration;
  backgroundResult.request.pairingGeneration = pairingGeneration;
  strncpy(backgroundResult.request.device, deviceId, sizeof(backgroundResult.request.device) - 1);
  backgroundResult.request.device[sizeof(backgroundResult.request.device) - 1] = '\0';
  backgroundResult.restore = restore;
  backgroundBusy = true;
  if (!backgroundTask) {
    executeBackgroundPoll(backgroundResult);
    consumeBackgroundPoll();
    return;
  }
  BackgroundResult* result = &backgroundResult;
  if (xQueueSend(backgroundRequests, &result, 0) != pdTRUE) {
    backgroundBusy = false;
    Serial.println("ERROR: background poll request queue is unavailable.");
  }
}

void consumeBackgroundPoll() {
  if (!backgroundBusy) return;
  BackgroundResult* result = &backgroundResult;
  if (backgroundTask && xQueueReceive(backgroundResults, &result, 0) != pdTRUE) return;
  backgroundBusy = false;
  if (WiFi.status() != WL_CONNECTED ||
      !BackgroundPoll::current(result->request.connectionGeneration, connectionGeneration)) return;
  if (result->request.kind == BackgroundPoll::Kind::Health) {
    if (!BackgroundPoll::current(result->request.stateGeneration, healthGeneration)) {
      if (result->restore) {
        if (serverOnline) lastDeviceStatusPollTime = millis() - deviceStatusPollingInterval;
        else statusRestoreNeeded = true;
      }
      return;
    }
    bool wasOnline = serverOnline;
    serverOnline = result->received && result->code == 200;
    if (serverOnline != wasOnline || result->restore || millis() - lastHealthLogTime > 60000) {
      lastHealthLogTime = millis();
      Serial.printf("Health: server %s (HTTP %d).\n", serverOnline ? "ONLINE" : "OFFLINE", result->code);
    }
    if (!serverOnline) roomStatusReceived = false;
    if (result->restore && serverOnline) lastDeviceStatusPollTime = millis() - deviceStatusPollingInterval;
    if (wasOnline != serverOnline && currentDisplayState == STATE_SCANNING) updateScanningDisplay();
    return;
  }
  if (strcmp(result->request.device, deviceId) != 0 ||
      !BackgroundPoll::current(result->request.pairingGeneration, pairingGeneration)) return;
  if (result->request.kind == BackgroundPoll::Kind::Status) {
    if (!serverOnline || !BackgroundPoll::current(result->request.stateGeneration, statusGeneration)) return;
    applyRoomStatusResponse(false, result->received, result->code, result->body);
    return;
  }
  if (!result->received || result->code != 200 || !extractJsonBool(result->body, "ok", false)) {
    Serial.printf("Buzzer reminder check failed (HTTP %d).\n", result->code);
    return;
  }
  String reminder = extractJsonString(result->body, "reminderId");
  String testValue = result->legacy ? "false" : extractJsonString(result->body, "test");
  bool reminderDue = reminder != "null" && BuzzerReminder::shouldPlay(reminder.c_str(), buzzerLastReminder);
  if (reminderDue) {
    strncpy(buzzerLastReminder, reminder.c_str(), sizeof(buzzerLastReminder) - 1);
    buzzerLastReminder[sizeof(buzzerLastReminder) - 1] = '\0';
  }
  if (testValue != "true" && testValue != "false")
    Serial.println("Buzzer test response is invalid.");
  if (reminderDue || testValue == "true") {
    if (buzzerMelodyTask) xTaskNotifyGive(buzzerMelodyTask);
    else Serial.println("ERROR: buzzer melody unavailable.");
  }
  if (reminder.length() == 0 || (reminder != "null" && !BuzzerReminder::validId(reminder.c_str())))
    Serial.println("Buzzer reminder response is invalid.");
}

void setup() {
  loadDeviceId();
  Serial.begin(SERIAL_BAUD);
  Serial.setTimeout(20);
  if (xTaskCreate(runBuzzerMelody, "buzzerMelody", 3072, nullptr, 1, &buzzerMelodyTask) != pdPASS) {
    buzzerMelodyTask = nullptr;
    Serial.println("ERROR: buzzer melody task could not start.");
  }
  backgroundRequests = xQueueCreate(1, sizeof(BackgroundResult*));
  backgroundResults = xQueueCreate(1, sizeof(BackgroundResult*));
  if (!backgroundRequests || !backgroundResults ||
      xTaskCreate(runBackgroundPoll, "networkPoll", 8192, nullptr, 1, &backgroundTask) != pdPASS) {
    backgroundTask = nullptr;
    if (backgroundRequests) vQueueDelete(backgroundRequests);
    if (backgroundResults) vQueueDelete(backgroundResults);
    backgroundRequests = nullptr;
    backgroundResults = nullptr;
    Serial.println("ERROR: background polling unavailable; using blocking polling.");
  }
  delay(300);
  // Pure ASCII so a wrong baud still "looks" like noise, but a correct baud is obvious.
  Serial.println();
  Serial.println();
  Serial.println("================================");
  Serial.println(" SmartClass device booting");
  Serial.print(" firmware ");
  Serial.println(FIRMWARE_VERSION);
  Serial.print(" USB Serial ");
  Serial.print(SERIAL_BAUD);
  Serial.println(" baud");
  Serial.println(" If you see boxes here, baud/wiring is wrong.");
  Serial.println("================================");
  Serial.flush();
  Serial.println(enrollmentAuthSelfTest() ? "Enrollment auth self-test passed (capture still disabled)." :
                                           "ERROR: enrollment auth self-test failed; capture remains disabled.");
  Serial.println(recoveryAuthSelfTest() ? "Recovery auth self-test passed (recovery still disabled)." :
                                         "ERROR: recovery auth self-test failed; recovery remains disabled.");

  if (AC_RELAY_PIN >= 0) {
    pinMode(AC_RELAY_PIN, OUTPUT);
    applyAcRelay();
  }

  lcd.init();
  lcd.backlight();
  lcd.createChar(0, acOnIcon);
  lcd.createChar(1, acOffIcon);

  // lcd.setCursor(0, 0);
  // lcd.print("Checking SIM900L");
  // lcd.setCursor(0, 1);
  // lcd.print("Please wait");
  // runGsmStartupCheck();

  lcd.setCursor(0, 0);
  lcd.print("SmartClass ");
  lcd.print(FIRMWARE_VERSION);
  lcd.setCursor(0, 1);
  lcd.print("Connecting WiFi");

  WiFi.begin(ssid, password);
#if AC_IR_ENABLED
  acIrSender.begin();
#endif
  unsigned long startAttemptTime = millis();
  const unsigned long wifiTimeout = 120000;

  while (WiFi.status() != WL_CONNECTED && millis() - startAttemptTime < wifiTimeout) {
    delay(500);
    Serial.print('.');
  }

  lcd.clear();
  if (WiFi.status() == WL_CONNECTED) {
    lcd.print("WiFi Connected!");
    Serial.println();
    Serial.print("WiFi IP: ");
    Serial.println(WiFi.localIP());
    configTime(gmtOffset_sec, daylightOffset_sec, ntpServer, "time.nist.gov");
    int retry = 0;
    while (sntp_get_sync_status() != SNTP_SYNC_STATUS_COMPLETED && retry < 20) {
      delay(500);
      retry++;
    }
    delay(500);
    lcd.clear();
    lcd.setCursor(0, 0);
    lcd.print("Sync room...");
    lcd.setCursor(0, 1);
    lcd.print(isDevicePaired() ? deviceId : "Not paired");
    checkServerHealth(true);
    if (serverOnline && isDevicePaired()) {
      restoreRoomStatusFromServer();
    }
    wifiWasConnected = true;
    delay(1200);
  } else {
    lcd.print("WiFi Timeout!");
    lcd.setCursor(0, 1);
    lcd.print("Booting Offline");
    Serial.println("\nWiFi connection failed within timeout. Continuing...");
    wifiWasConnected = false;
    statusRestoreNeeded = true;
    delay(2000);
  }

  Serial.println("Starting fingerprint UART on Serial2...");
  Serial.printf("  RX=GPIO%d TX=GPIO%d @ %d (do NOT use GPIO1/3 — those are USB)\n",
                FINGERPRINT_RX_PIN, FINGERPRINT_TX_PIN, FINGERPRINT_BAUD);
  beginFingerprintUart();
  if (!finger.verifyPassword()) {
    lcd.clear();
    lcd.print("Sensor Error!");
    Serial.println("Fingerprint Sensor handshake failure.");
    Serial.println("Check sensor 5V/GND and TX->GPIO16, RX->GPIO17 (crossed).");
    while (1) { delay(1000); }
  }

  dht.begin();
  keypad.setDebounceTime(40);
  keypad.setHoldTime(MENU_BACK_HOLD_MS);
  Serial.println();
  Serial.println("=== SmartClass device ready ===");
  Serial.printf("firmware=%s\n", FIRMWARE_VERSION);
  Serial.printf("baseUrl=%s\n", baseUrl);
  Serial.printf("deviceId=%s\n", deviceId);
  Serial.println("Keypad rows GPIO 14,32,33,26  cols GPIO 27,12,25");
  Serial.println("Keypad: press # for Admin / Faculty menu.");
  printSensorStatus();
  Serial.println("Default mode: SCANNING (place finger anytime to log via API).");
  Serial.println("Serial commands: ENROLL | DELETEALL | STATUS | HEALTH | GSM | HELP");
  enterScanningMode();
}

void loop() {
  unsigned long currentMillis = millis();

  processSerialCommands();
  if (provisioningKey) {
    delay(1);
    return;
  }

  if (WiFi.status() != WL_CONNECTED) {
    if (wifiWasConnected) ++connectionGeneration;
    wifiWasConnected = false;
    statusRestoreNeeded = true;
    serverOnline = false;
    roomStatusReceived = false;
    if (currentMillis - lastWiFiRetryTime >= wifiRetryInterval) {
      lastWiFiRetryTime = currentMillis;
      Serial.println("Attempting WiFi reconnection...");
      WiFi.disconnect();
      WiFi.begin(ssid, password);
    }
  } else if (!wifiWasConnected || statusRestoreNeeded) {
    wifiWasConnected = true;
    if (!backgroundBusy) {
      statusRestoreNeeded = false;
      lastHealthCheckTime = millis();
      scheduleBackgroundPoll(BackgroundPoll::Kind::Health, true);
    }
  }

  consumeBackgroundPoll();
  if (WiFi.status() == WL_CONNECTED && !backgroundBusy && !statusRestoreNeeded) {
    unsigned long now = millis();
    BackgroundPoll::Kind next = BackgroundPoll::next(now, lastHealthCheckTime, healthCheckInterval,
        lastDeviceStatusPollTime, deviceStatusPollingInterval, buzzerLastPoll, BUZZER_POLL_MS,
        serverOnline, isDevicePaired());
    if (next != BackgroundPoll::Kind::None) {
      if (next == BackgroundPoll::Kind::Health) lastHealthCheckTime = now;
      else if (next == BackgroundPoll::Kind::Status) lastDeviceStatusPollTime = now;
      else buzzerLastPoll = now;
      scheduleBackgroundPoll(next);
    }
  }

#if AC_IR_ENABLED
  if (currentDisplayState == STATE_SCANNING && WiFi.status() == WL_CONNECTED && serverOnline &&
      isDevicePaired() && !acIrHalted && AcIr::pollDue(millis(), acIrLastPoll)) {
    acIrLastPoll = millis();
    pollAcIr();
  }
#endif
  // Enroll owns the sensor and the LCD until it finishes.
  if (currentDisplayState == STATE_ENROLL) {
    return;
  }

  pollKeypad(currentMillis);

  // Menu owns the LCD; pause fingerprint scans until the user exits.
  if (isKeypadMenuState(currentDisplayState)) {
    return;
  }

  manageDisplayState(currentMillis);

  if (currentDisplayState == STATE_SCANNING) {
#if ENROLLMENT_WRITES_ENABLED
    if (WiFi.status() == WL_CONNECTED && serverOnline && fingerprintEnrollmentEnabled &&
        !waitForEnrollmentFingerRemoval && millis() - lastInventoryAttempt >= INVENTORY_INTERVAL_MS) {
      lastInventoryAttempt = millis();
      reportSensorInventory();
    }
#endif
    if (currentMillis - lastTimeDisplayUpdate >= timeDisplayInterval) {
      lastTimeDisplayUpdate = currentMillis;
      showColon = !showColon;
      updateScanningDisplay();
    }
    if (currentMillis - lastDHTReadTime >= dhtInterval) {
      lastDHTReadTime = currentMillis;
      updateScanningDisplay();
    }

    pollScanAndLog();
  }
}

bool sendGsmCommand(const char* command, const char* expected, uint32_t timeoutMs, String& response) {
  while (gsmSerial.available() > 0) {
    gsmSerial.read();
  }

  gsmSerial.print(command);
  gsmSerial.print('\r');
  response = "";
  unsigned long startedAt = millis();
  while (millis() - startedAt < timeoutMs) {
    while (gsmSerial.available() > 0) {
      char c = (char)gsmSerial.read();
      if (c != '\r') {
        response += c;
      }
    }
    if (response.indexOf(expected) >= 0 || response.indexOf("ERROR") >= 0) {
      break;
    }
    delay(10);
  }
  return response.indexOf(expected) >= 0;
}

void runGsmStartupCheck() {
  Serial.println();
  Serial.println("=== SIM900L startup check ===");
  Serial.printf("Starting GSM UART1: RX=GPIO%d TX=GPIO%d @ %d baud\n",
                GSM_RX_PIN, GSM_TX_PIN, GSM_BAUD);
  gsmSerial.end();
  delay(20);
  gsmSerial.begin(GSM_BAUD, SERIAL_8N1, GSM_RX_PIN, GSM_TX_PIN);

  String response;
  for (uint8_t attempt = 1; attempt <= 10 && !gsmResponding; attempt++) {
    gsmResponding = sendGsmCommand("AT", "OK", 1000, response);
    if (!gsmResponding) {
      Serial.printf("Modem AT check %u/10: no response\n", attempt);
      delay(300);
    }
  }

  if (!gsmResponding) {
    Serial.println("GSM modem NOT responding. Check power, common GND, crossed UART wires, and 9600 baud.");
    Serial.println("Continuing startup; fingerprint and WiFi will still be available.");
    return;
  }
  Serial.println("GSM modem AT: OK");

  if (sendGsmCommand("ATE0", "OK", 1500, response)) {
    Serial.println("Command echo: disabled");
  } else {
    Serial.printf("Command echo setup response: %s\n", response.c_str());
  }
  if (sendGsmCommand("ATI", "OK", 2000, response)) {
    Serial.printf("Modem identity: %s\n", response.c_str());
  } else {
    Serial.printf("Modem identity query failed: %s\n", response.c_str());
  }

  if (sendGsmCommand("AT+CPIN?", "OK", 2000, response) && response.indexOf("READY") >= 0) {
    gsmSimReady = true;
    Serial.println("SIM card: READY");
  } else {
    Serial.printf("SIM card: not ready (%s)\n", response.c_str());
  }

  if (gsmSimReady) {
    if (sendGsmCommand("AT+CNUM", "OK", 3000, response)) {
      if (response.indexOf("+CNUM:") >= 0) {
        Serial.printf("SIM phone number (if provisioned): %s\n", response.c_str());
      } else {
        Serial.println("SIM phone number: not provided by SIM/network (AT+CNUM returned no number).");
      }
    } else {
      Serial.printf("SIM phone number query unavailable: %s\n", response.c_str());
    }
  }

  if (gsmSimReady) {
    const unsigned long registrationWaitMs = 30000;
    unsigned long registrationStartedAt = millis();
    do {
      if (sendGsmCommand("AT+CREG?", "OK", 2000, response)) {
        int comma = response.lastIndexOf(',');
        if (comma >= 0 && comma + 1 < (int)response.length()) {
          char registrationStatus = response[comma + 1];
          gsmNetworkRegistered = registrationStatus == '1' || registrationStatus == '5';
        }
      }
      if (gsmNetworkRegistered) {
        break;
      }
      if (millis() - registrationStartedAt < registrationWaitMs) {
        delay(3000);
      }
    } while (millis() - registrationStartedAt < registrationWaitMs);

    Serial.printf("Network registration: %s (%s)\n",
                  gsmNetworkRegistered ? "REGISTERED" : "NOT REGISTERED",
                  response.c_str());
  }

  if (sendGsmCommand("AT+CSQ", "OK", 2000, response)) {
    Serial.printf("Signal quality: %s\n", response.c_str());
  } else {
    Serial.printf("Signal quality query failed: %s\n", response.c_str());
  }

  Serial.println("Startup check complete.");
}

void beginFingerprintUart() {
  // Adafruit_Fingerprint::begin(baud) calls mySerial->begin(baud) WITHOUT ESP32 pin
  // args and can leave UART2 on the wrong pins. Always re-bind RX/TX afterward.
  mySerial.end();
  delay(20);
  mySerial.begin(FINGERPRINT_BAUD, SERIAL_8N1, FINGERPRINT_RX_PIN, FINGERPRINT_TX_PIN);
  delay(100);
  // Library begin() also delays ~1s and re-opens the UART — call then re-bind pins.
  finger.begin(FINGERPRINT_BAUD);
  mySerial.end();
  delay(20);
  mySerial.begin(FINGERPRINT_BAUD, SERIAL_8N1, FINGERPRINT_RX_PIN, FINGERPRINT_TX_PIN);
  delay(50);
}

void enterScanningMode() {
  clearKeypadEntry();
  memset(adminPinSession, 0, sizeof(adminPinSession));
  memset(facultyIdBuf, 0, sizeof(facultyIdBuf));
  mbedtls_platform_zeroize(facultySession, sizeof(facultySession));
  fingerDownHandled = false;
  currentDisplayState = STATE_SCANNING;
  lcd.clear();
  updateScanningDisplay();
  Serial.println("Scanning mode ready — waiting for fingerprint.");
}

// ---------------------------------------------------------------------------
// Keypad menu. # from the ready screen asks Admin vs Faculty teacher.
// Lists use * previous, # next, 0 select, and hold 0 to go back.
// Admin: PIN. Faculty: mobile number then SMS OTP.
// ---------------------------------------------------------------------------
bool isKeypadMenuState(DisplayState state) {
  return state == STATE_MENU_ROLE ||
         state == STATE_MENU_ADMIN_PIN ||
         state == STATE_MENU_FACULTY_ID ||
         state == STATE_MENU_FACULTY_PIN ||
         state == STATE_MENU_ADMIN ||
         state == STATE_MENU_FACULTY ||
         state == STATE_MENU_FACULTY_SCHEDULE ||
         state == STATE_MENU_FACULTY_AC ||
         state == STATE_MENU_ROOMS ||
         state == STATE_MENU_PAIR_CODE ||
         state == STATE_MENU_UNPAIR_CONFIRM ||
         state == STATE_MENU_NOTICE;
}

void printLcdRow(uint8_t row, const char* text) {
  char buf[17];
  snprintf(buf, sizeof(buf), "%-16.16s", text);
  lcd.setCursor(0, row);
  lcd.print(buf);
}

void clearKeypadEntry() {
  memset(entryBuf, 0, sizeof(entryBuf));
  entryLen = 0;
}

void formatKeypadEntryLine(char* out, size_t outLen, bool mask) {
  char vis[MENU_ENTRY_MAX + 1];
  uint8_t n = entryLen > MENU_ENTRY_MAX ? MENU_ENTRY_MAX : entryLen;
  for (uint8_t i = 0; i < n; i++) {
    vis[i] = mask ? '*' : entryBuf[i];
  }
  vis[n] = '\0';
  if (currentDisplayState == STATE_MENU_FACULTY_ID)
    snprintf(out, outLen, "%-11s *#", vis);
  else
    snprintf(out, outLen, "%-6s *del #ok", vis);
}

void renderEntryLine() {
  char line[17];
  bool mask = (currentDisplayState != STATE_MENU_FACULTY_ID && currentDisplayState != STATE_MENU_PAIR_CODE);
  formatKeypadEntryLine(line, sizeof(line), mask);
  printLcdRow(1, line);
}

bool isPickerMenu(DisplayState state) {
  return state == STATE_MENU_ROLE ||
         state == STATE_MENU_ADMIN ||
         state == STATE_MENU_FACULTY ||
         state == STATE_MENU_FACULTY_SCHEDULE ||
         state == STATE_MENU_FACULTY_AC ||
         state == STATE_MENU_ROOMS;
}

uint8_t menuItemCount(DisplayState state) {
  if (state == STATE_MENU_ROLE) {
    return 2;
  }
  if (state == STATE_MENU_ADMIN) {
#if ENROLLMENT_WRITES_ENABLED && REPLACEMENT_WRITES_ENABLED
    return 7;
#else
    return 6;
#endif
  }
  if (state == STATE_MENU_FACULTY) {
    return 2;
  }
  if (state == STATE_MENU_ROOMS) {
    return roomOptionCount + (roomOptionHasCurrent ? 1 : 0);
  }
  return 0;
}

const char* menuItemLabel(DisplayState state, uint8_t index) {
  if (state == STATE_MENU_ROLE) {
    return index == 0 ? "Admin" : "Faculty";
  }
  if (state == STATE_MENU_ADMIN) {
    if (index == 0) return "Assign room";
    if (index == 1) return "Enroll finger";
    if (index == 2) return "Wipe sensor";
    if (index == 3) return "Room status";
#if ENROLLMENT_WRITES_ENABLED && REPLACEMENT_WRITES_ENABLED
    if (index == 5) return "Replace finger";
    if (index == 6) return "Unpair device";
#else
    if (index == 5) return "Unpair device";
#endif
    return "Pair device";
  }
  if (state == STATE_MENU_ROOMS) {
    return index < roomOptionCount ? roomOptionLabels[index] : "Unassign";
  }
  if (index == 0) return "My schedule";
  return "AC info";
}

void renderPickerTitle() {
  char line[17] = "> ";
  size_t used = 2;
  const unsigned char* text = (const unsigned char*)menuItemLabel(currentDisplayState, menuIndex);
  while (*text && used < sizeof(line) - 1) {
    unsigned char ch = *text++;
    if (ch >= 32 && ch <= 126) {
      line[used++] = (char)ch;
    } else if ((ch & 0xC0) != 0x80) {
      line[used++] = '?';
    }
  }
  line[used] = '\0';
  printLcdRow(0, line);
}

void renderMenu() {
  lcd.clear();
  switch (currentDisplayState) {
    case STATE_MENU_ROLE:
    case STATE_MENU_ADMIN:
    case STATE_MENU_FACULTY:
    case STATE_MENU_ROOMS:
      renderPickerTitle();
      printLcdRow(1, "<*    0 OK    #>");
      break;
    case STATE_MENU_ADMIN_PIN:
      printLcdRow(0, "Admin PIN:");
      renderEntryLine();
      break;
    case STATE_MENU_PAIR_CODE:
      printLcdRow(0, "Pairing code:");
      renderEntryLine();
      break;
    case STATE_MENU_UNPAIR_CONFIRM:
      printLcdRow(0, "Unpair locally?");
      printLcdRow(1, "*Cancel #Confirm");
      break;
    case STATE_MENU_FACULTY_ID:
      printLcdRow(0, "Mobile 09...:");
      renderEntryLine();
      break;
    case STATE_MENU_FACULTY_PIN:
      printLcdRow(0, "SMS code:");
      renderEntryLine();
      break;
    case STATE_MENU_FACULTY_SCHEDULE:
      renderFacultySchedule();
      break;
    case STATE_MENU_FACULTY_AC:
      renderFacultyAc();
      break;
    default:
      break;
  }
}

void showMenuNotice(const char* line0, const char* line1, DisplayState returnState,
                    bool clearEntry, bool clearFaculty, unsigned long duration) {
  menuReturnState = returnState;
  menuNoticeClearEntry = clearEntry;
  menuNoticeClearFaculty = clearFaculty;
  menuNoticeDuration = duration;
  currentDisplayState = STATE_MENU_NOTICE;
  stateTimer = millis();
  menuActivityAt = millis();
  lcd.clear();
  printLcdRow(0, line0);
  printLcdRow(1, line1);
  Serial.printf("Keypad notice: %s | %s\n", line0, line1);
}

void startEnrollment() {
  clearKeypadEntry();
  memset(adminPinSession, 0, sizeof(adminPinSession));
  currentDisplayState = STATE_ENROLL;
#if ENROLLMENT_WRITES_ENABLED
  lcd.clear();
  printLcdRow(0, "Checking enroll");
  printLcdRow(1, "Please wait");
  EnrollmentNotice::Message notice = runEnrollmentFromServer();
  waitForEnrollmentFingerRemoval = true;
  showMenuNotice(notice.line0, notice.line1, STATE_SCANNING, true, true, ENROLLMENT_NOTICE_MS);
#else
  Serial.println("Fingerprint enrollment is disabled in this build; no enrollment sensor write was attempted.");
  showMenuNotice("Enroll disabled", "Contact admin", STATE_SCANNING, true, true, ENROLLMENT_NOTICE_MS);
#endif
}

#if ENROLLMENT_WRITES_ENABLED && REPLACEMENT_WRITES_ENABLED
void startReplacement() {
  clearKeypadEntry();
  memset(adminPinSession, 0, sizeof(adminPinSession));
  currentDisplayState = STATE_ENROLL;
  lcd.clear();
  printLcdRow(0, "Checking replace");
  printLcdRow(1, "Please wait");
  EnrollmentJournalStorage storage;
  EnrollmentJournal::Record pending{};
  auto status = storage.read(pending);
  EnrollmentNotice::Message notice = status == EnrollmentJournal::ReadStatus::Empty
      ? runReplacementOnce() : runEnrollmentFromServer();
  waitForEnrollmentFingerRemoval = true;
  showMenuNotice(notice.line0, notice.line1, STATE_SCANNING, true, true, ENROLLMENT_NOTICE_MS);
}
#endif

void closeMenuNotice() {
  if (menuNoticeClearEntry) {
    clearKeypadEntry();
  }
  if (menuNoticeClearFaculty) {
    memset(facultyIdBuf, 0, sizeof(facultyIdBuf));
  }
  menuNoticeClearEntry = false;
  menuNoticeClearFaculty = false;

  if (menuReturnState == STATE_SCANNING) {
    enterScanningMode();
    return;
  }
  currentDisplayState = menuReturnState;
  menuActivityAt = millis();
  renderMenu();
}

void openRoleMenu() {
  clearKeypadEntry();
  memset(facultyIdBuf, 0, sizeof(facultyIdBuf));
  menuIndex = 0;
  menuActivityAt = millis();
  currentDisplayState = STATE_MENU_ROLE;
  Serial.println("Keypad: role menu — * previous, # next, 0 select, hold 0 back.");
  renderMenu();
}

void menuMove(int delta) {
  uint8_t count = menuItemCount(currentDisplayState);
  if (count == 0 || delta == 0) {
    return;
  }
  int next = (int)menuIndex + delta;
  while (next < 0) {
    next += count;
  }
  while (next >= (int)count) {
    next -= count;
  }
  const char* toLabel = menuItemLabel(currentDisplayState, (uint8_t)next);
  menuIndex = (uint8_t)next;
  renderPickerTitle();
  Serial.printf("Keypad highlight: %s\n", toLabel);
}

void exitPickerMenu() {
  if (currentDisplayState == STATE_MENU_FACULTY_SCHEDULE || currentDisplayState == STATE_MENU_FACULTY_AC) {
    currentDisplayState = STATE_MENU_FACULTY;
    menuIndex = 0;
    renderMenu();
    return;
  }
  if (currentDisplayState == STATE_MENU_ROOMS) {
    menuIndex = 0;
    currentDisplayState = STATE_MENU_ADMIN;
    renderMenu();
    return;
  }
  if (currentDisplayState == STATE_MENU_ADMIN) {
    Serial.println("Keypad: admin logged out.");
  } else if (currentDisplayState == STATE_MENU_FACULTY) {
    Serial.println("Keypad: faculty logged out.");
  }
  enterScanningMode();
}

// Returns the HTTP status (200 on success) or -1 when the server is unreachable.
// Wrong PINs, lockouts, and unknown devices come back as 401 / 429 / 404.
int loadAdminRooms(String& responseBody) {
  int httpCode = -1;
  String payload = String("{\"deviceId\":\"") + deviceId + "\",\"adminPin\":\"" + adminPinSession + "\"}";
  if (!httpPostJson("/api/device/admin/rooms", payload, responseBody, httpCode, false) || httpCode <= 0) {
    return -1;
  }
  if (httpCode < 200 || httpCode >= 300 || !extractJsonBool(responseBody, "ok", false)) {
    return httpCode;
  }

  roomOptionCount = 0;
  roomOptionHasCurrent = false;
  String list = extractJsonString(responseBody, "roomList");
  int start = 0;
  while (start < (int)list.length() && roomOptionCount < MAX_ROOM_OPTIONS) {
    int end = list.indexOf(';', start);
    if (end < 0) {
      break;
    }
    String entry = list.substring(start, end);
    start = end + 1;
    int firstBar = entry.indexOf('|');
    int lastBar = entry.lastIndexOf('|');
    if (firstBar <= 0 || lastBar <= firstBar) {
      continue;
    }
    bool current = entry.substring(lastBar + 1).toInt() == 1;
    String name = entry.substring(firstBar + 1, lastBar);
    roomOptionIds[roomOptionCount] = entry.substring(0, firstBar).toInt();
    snprintf(roomOptionLabels[roomOptionCount], sizeof(roomOptionLabels[0]), "%.12s%s", name.c_str(), current ? "*" : "");
    if (current) {
      roomOptionHasCurrent = true;
    }
    roomOptionCount++;
  }
  return 200;
}

void showAdminRequestFailure(int httpCode, const String& responseBody) {
  if (httpCode == 401) {
    memset(adminPinSession, 0, sizeof(adminPinSession));
    denyMenuAuth(STATE_MENU_ADMIN_PIN);
  } else if (httpCode == 429) {
    showMenuNotice("Locked out", "Try again later", STATE_MENU_ADMIN_PIN, true, false);
  } else if (httpCode == -1) {
    showMenuNotice("Server offline", "Try again", STATE_MENU_ADMIN_PIN, true, false);
  } else {
    String error = extractJsonString(responseBody, "error");
    showMenuNotice("Request failed", error.length() > 0 ? error.c_str() : "Try again", STATE_MENU_ADMIN, false, false);
  }
}

void openRoomPicker() {
  printLcdRow(0, "Loading rooms...");
  printLcdRow(1, "");
  String responseBody;
  int httpCode = loadAdminRooms(responseBody);
  if (httpCode != 200) {
    showAdminRequestFailure(httpCode, responseBody);
    return;
  }
  if (menuItemCount(STATE_MENU_ROOMS) == 0) {
    showMenuNotice("No free rooms", "Add one in portal", STATE_MENU_ADMIN, false, false);
    return;
  }
  menuIndex = 0;
  for (uint8_t i = 0; i < roomOptionCount; i++) {
    if (strchr(roomOptionLabels[i], '*') != nullptr) {
      menuIndex = i;
    }
  }
  currentDisplayState = STATE_MENU_ROOMS;
  renderMenu();
}

void assignSelectedRoom() {
  bool unassign = menuIndex >= roomOptionCount;
  char label[15];
  snprintf(label, sizeof(label), "%s", menuItemLabel(STATE_MENU_ROOMS, menuIndex));
  String classroomId = unassign ? String("null") : String(roomOptionIds[menuIndex]);
  String payload = String("{\"deviceId\":\"") + deviceId + "\",\"adminPin\":\"" + adminPinSession +
                   "\",\"classroomId\":" + classroomId + "}";
  printLcdRow(0, "Saving...");
  printLcdRow(1, "");
  String responseBody;
  int httpCode = -1;
  if (!httpPostJson("/api/device/admin/assign", payload, responseBody, httpCode, false) || httpCode <= 0) {
    showAdminRequestFailure(-1, responseBody);
    return;
  }
  if (httpCode < 200 || httpCode >= 300 || !extractJsonBool(responseBody, "ok", false)) {
    showAdminRequestFailure(httpCode, responseBody);
    return;
  }

  Serial.printf("Keypad: device %s -> %s\n", deviceId, unassign ? "unassigned" : label);
  applyRoomStatusFromServer(false);
  menuIndex = 0;
  if (unassign) {
    showMenuNotice("Unassigned", "Room removed", STATE_MENU_ADMIN, false, false);
  } else {
    String roomName = extractJsonString(responseBody, "classroomName");
    showMenuNotice("Room assigned", roomName.c_str(), STATE_MENU_ADMIN, false, false);
  }
}

void selectHighlightedItem() {
  if (currentDisplayState == STATE_MENU_ROOMS) {
    assignSelectedRoom();
    return;
  }

  if (currentDisplayState == STATE_MENU_ROLE) {
    clearKeypadEntry();
    if (menuIndex == 0) {
      currentDisplayState = STATE_MENU_ADMIN_PIN;
    } else {
      memset(facultyIdBuf, 0, sizeof(facultyIdBuf));
      currentDisplayState = STATE_MENU_FACULTY_ID;
    }
    renderMenu();
    return;
  }

  if (currentDisplayState == STATE_MENU_ADMIN) {
    if (menuIndex == 0) {
      if (!isDevicePaired()) {
        showMenuNotice("Not paired", "Use Pair device", STATE_MENU_ADMIN, false, false);
      } else {
        openRoomPicker();
      }
    } else if (menuIndex == 1) {
      startEnrollment();
    } else if (menuIndex == 2) {
      showMenuNotice("Wipe sensor", "Coming soon", STATE_MENU_ADMIN, false, false);
    } else if (menuIndex == 3) {
      showMenuNotice("Room status", "Coming soon", STATE_MENU_ADMIN, false, false);
    } else if (menuIndex == 4) {
      openPairCodeEntry();
#if ENROLLMENT_WRITES_ENABLED && REPLACEMENT_WRITES_ENABLED
    } else if (menuIndex == 5) {
      startReplacement();
#endif
    } else {
      if (!isDevicePaired()) {
        showMenuNotice("Not paired", "No ID to clear", STATE_MENU_ADMIN, false, false);
      } else {
        currentDisplayState = STATE_MENU_UNPAIR_CONFIRM;
        Serial.println("Local unpair confirmation: portal pairing and sensor fingerprints will remain unchanged.");
        renderMenu();
      }
    }
    return;
  }

  if (menuIndex == 0) {
    loadFacultySchedule(0);
  } else {
    loadFacultyAc();
  }
}

void menuAppendDigit(char key) {
  uint8_t limit = currentDisplayState == STATE_MENU_FACULTY_ID ? MENU_ENTRY_MAX : MENU_SHORT_ENTRY_MAX;
  if (entryLen >= limit) {
    return;
  }
  entryBuf[entryLen++] = key;
  entryBuf[entryLen] = '\0';
  renderEntryLine();
}

void menuBackspace() {
  if (entryLen > 0) {
    entryLen--;
    entryBuf[entryLen] = '\0';
    renderEntryLine();
    return;
  }

  if (currentDisplayState == STATE_MENU_FACULTY_PIN) {
    strncpy(entryBuf, facultyIdBuf, sizeof(entryBuf) - 1);
    entryBuf[sizeof(entryBuf) - 1] = '\0';
    entryLen = (uint8_t)strlen(entryBuf);
    currentDisplayState = STATE_MENU_FACULTY_ID;
    renderMenu();
    return;
  }

  if (currentDisplayState == STATE_MENU_PAIR_CODE) {
    currentDisplayState = isDevicePaired() ? STATE_MENU_ADMIN : STATE_MENU_ROLE;
    renderMenu();
    return;
  }

  currentDisplayState = STATE_MENU_ROLE;
  renderMenu();
}

void denyMenuAuth(DisplayState retryState) {
  bool faculty = (retryState == STATE_MENU_FACULTY_ID);
  Serial.println(faculty ? "Keypad: faculty auth failed." : "Keypad: admin auth failed.");
  showMenuNotice("Access denied",
                 faculty ? "Invalid code" : "Wrong PIN",
                 retryState,
                 true,
                 faculty);
}

void submitAdminPin() {
  if (entryLen < ADMIN_PIN_LENGTH) {
    showMenuNotice("Need 6 digits", "Then press #", STATE_MENU_ADMIN_PIN, false, false);
    return;
  }
  strncpy(adminPinSession, entryBuf, sizeof(adminPinSession) - 1);
  adminPinSession[sizeof(adminPinSession) - 1] = '\0';
  if (!isDevicePaired()) {
    // No device ID yet: the server verifies the PIN together with the pairing code.
    Serial.println("Keypad: device not paired, ask pairing code.");
    openPairCodeEntry();
    return;
  }
  printLcdRow(0, "Checking PIN...");
  printLcdRow(1, "");
  String responseBody;
  int httpCode = loadAdminRooms(responseBody);
  if (httpCode == 404 &&
      extractJsonString(responseBody, "error") == "Unknown or inactive biometric device.") {
    roomStatusReceived = false;
    Serial.println("Keypad: server rejected saved device ID; check portal activity and pairing.");
    openPairCodeEntry();
    return;
  }
  if (httpCode != 200) {
    memset(adminPinSession, 0, sizeof(adminPinSession));
    showAdminRequestFailure(httpCode, responseBody);
    return;
  }
  clearKeypadEntry();
  menuIndex = 0;
  Serial.println("Keypad: admin authenticated by server.");
  showMenuNotice("Admin OK", "Choose option", STATE_MENU_ADMIN, false, false);
}

void openPairCodeEntry() {
  if (!enrollmentJournalIsClear()) {
    memset(adminPinSession, 0, sizeof(adminPinSession));
    Serial.println("Cannot start pairing: enrollment journal is pending or unavailable.");
    showMenuNotice("Write pending", "Pair disabled", STATE_MENU_ADMIN_PIN, true, false);
    return;
  }
  if (!devicePrefs.begin("smartclass", true)) {
    memset(adminPinSession, 0, sizeof(adminPinSession));
    showMenuNotice("Storage error", "Cannot pair", STATE_MENU_ADMIN_PIN, true, false);
    return;
  }
  bool hasEnrollmentKey = devicePrefs.isKey("enrollKey");
  devicePrefs.end();
  if (hasEnrollmentKey) {
    memset(adminPinSession, 0, sizeof(adminPinSession));
    Serial.println("Cannot start pairing: enrollment key installed; preserve key and investigate server pairing.");
    showMenuNotice("Key installed", "Pair disabled", STATE_MENU_ADMIN_PIN, true, false);
    return;
  }
  clearKeypadEntry();
  currentDisplayState = STATE_MENU_PAIR_CODE;
  renderMenu();
}

void unpairDeviceLocally() {
  if (!isDevicePaired()) {
    showMenuNotice("Not paired", "No ID to clear", STATE_MENU_ADMIN, false, false);
    return;
  }
  if (!enrollmentJournalIsClear()) {
    Serial.println("Local unpair refused: enrollment journal is pending or unavailable.");
    showMenuNotice("Write pending", "Unpair blocked", STATE_MENU_ADMIN, false, false);
    return;
  }
  if (!devicePrefs.begin("smartclass", false)) {
    Serial.println("Local unpair failed: preferences unavailable.");
    showMenuNotice("Storage error", "ID retained", STATE_MENU_ADMIN, false, false);
    return;
  }
  if (devicePrefs.isKey("enrollKey")) {
    devicePrefs.end();
    Serial.println("Local unpair refused: enrollment key installed; preserve provisioning.");
    showMenuNotice("Key installed", "Unpair blocked", STATE_MENU_ADMIN, false, false);
    return;
  }
  bool removed = devicePrefs.remove("deviceId");
  bool absent = !devicePrefs.isKey("deviceId");
  devicePrefs.end();
  if (!removed || !absent) {
    Serial.println("Local unpair failed: saved device ID removal was not verified.");
    showMenuNotice("Storage error", "Restart / check", STATE_MENU_ADMIN, false, false);
    return;
  }
  memset(deviceId, 0, sizeof(deviceId));
  ++pairingGeneration;
  ++statusGeneration;
  roomStatusReceived = false;
  assignedRoomName = "";
  clearActiveTeacher();
#if AC_IR_ENABLED
  acIrRegistered = false;
  acIrHalted = false;
#endif
  memset(adminPinSession, 0, sizeof(adminPinSession));
  clearKeypadEntry();
  mbedtls_platform_zeroize(facultySession, sizeof(facultySession));
  Serial.println("Device unpaired locally. Portal pairing, room assignment, sensor fingerprints and AC power are unchanged.");
  showMenuNotice("Locally unpaired", "Portal unchanged", STATE_SCANNING, true, true, 3000);
}

void submitPairCode() {
  if (entryLen < 6) {
    showMenuNotice("Need 6 digits", "Then press #", STATE_MENU_PAIR_CODE, false, false);
    return;
  }
  printLcdRow(0, "Pairing...");
  printLcdRow(1, "");
  String payload = String("{\"adminPin\":\"") + adminPinSession + "\",\"pairingCode\":\"" + entryBuf + "\"}";
  String responseBody;
  int httpCode = -1;
  bool reached = httpPostJson("/api/device/admin/pair", payload, responseBody, httpCode, false) && httpCode > 0;
  if (!reached) {
    showMenuNotice("Server offline", "Try again", STATE_MENU_PAIR_CODE, true, false);
    return;
  }
  if (httpCode == 429) {
    memset(adminPinSession, 0, sizeof(adminPinSession));
    showMenuNotice("Locked out", "Try after 5 min", STATE_MENU_ADMIN_PIN, true, false);
    return;
  }
  if (httpCode == 401) {
    String error = extractJsonString(responseBody, "error");
    memset(adminPinSession, 0, sizeof(adminPinSession));
    if (error == "Unauthorized device.") {
      showMenuNotice("API key invalid", "Check settings", STATE_MENU_ADMIN_PIN, true, false);
    } else {
      showMenuNotice("Invalid PIN/code", "Check both", STATE_MENU_ADMIN_PIN, true, false);
    }
    return;
  }
  if (httpCode == 404 || httpCode == 405) {
    showMenuNotice("Pair API missing", "Update server", STATE_MENU_PAIR_CODE, true, false);
    return;
  }
  if (httpCode < 200 || httpCode >= 300) {
    showMenuNotice("Server error", "Check server log", STATE_MENU_PAIR_CODE, true, false);
    return;
  }
  String newId = extractJsonString(responseBody, "deviceId");
  bool validId = newId.length() > 0 && newId.length() < sizeof(deviceId);
  for (size_t i = 0; i < newId.length(); i++) {
    if (newId[i] < '0' || newId[i] > '9') validId = false;
  }
  if (!extractJsonBool(responseBody, "ok", false) || !validId) {
    showMenuNotice("Bad response", "Check server", STATE_MENU_PAIR_CODE, true, false);
    return;
  }

  if (!saveDeviceId(newId)) {
    memset(adminPinSession, 0, sizeof(adminPinSession));
    showMenuNotice("ID save failed", "Need new code", STATE_MENU_ADMIN_PIN, true, false);
    return;
  }
  roomStatusReceived = false;
  assignedRoomName = "";
  Serial.printf("Keypad: paired as device %s\n", deviceId);
  applyRoomStatusFromServer(false);
  clearKeypadEntry();
  menuIndex = 0;
  showMenuNotice("Paired", deviceId, STATE_MENU_ADMIN, false, false);
}

void submitFacultyId() {
  if (!FacultyOtp::validValue(entryBuf, true)) {
    showMenuNotice("Use 09 + 9 digits", "Then press #", STATE_MENU_FACULTY_ID, false, false);
    return;
  }
  lcd.clear();
  printLcdRow(0, "Requesting code");
  printLcdRow(1, "Please wait");
  FacultyOtp::Result result = FacultyOtp::Result::Denied;
  bool ok = requestFacultyOtp(true, entryBuf, result);
  if (ok && result == FacultyOtp::Result::Processed) {
    strncpy(facultyIdBuf, entryBuf, sizeof(facultyIdBuf) - 1);
    facultyIdBuf[sizeof(facultyIdBuf) - 1] = '\0';
  }
  clearKeypadEntry();
  menuActivityAt = millis();
  if (!ok || result != FacultyOtp::Result::Processed) {
    showMenuNotice("Request failed", "Try again later", STATE_MENU_FACULTY_ID, true, true);
    return;
  }
  currentDisplayState = STATE_MENU_FACULTY_PIN;
  renderMenu();
}

bool postFacultyOtpJson(const char* path, const String& body, String& response, int& code, uint16_t timeoutMs) {
  response = "";
  code = -1;
  if (WiFi.status() != WL_CONNECTED) return false;
  String url = String(baseUrl) + path;
  WiFiClient plainClient;
  WiFiClientSecure secureClient;
  HTTPClient http;
  http.setConnectTimeout(8000);
  http.setTimeout(timeoutMs);
  bool started = false;
  if (url.startsWith("https://") && facultyOtpCaCertificate[0] != '\0') {
    secureClient.setCACert(facultyOtpCaCertificate);
    started = http.begin(secureClient, url);
  } else if (url.startsWith("http://") && FACULTY_OTP_ALLOW_HTTP) {
    started = http.begin(plainClient, url);
  }
  if (!started) return false;
  http.addHeader("Content-Type", "application/json");
  http.addHeader("X-Device-Api-Key", deviceApiKey);
  code = http.POST(body);
  if (code > 0) response = http.getString();
  http.end();
  return code > 0 && response.length() <= 512;
}

bool requestFacultyOtp(bool issuing, const char* value, FacultyOtp::Result& result) {
  if (!isDevicePaired() || !FacultyOtp::validValue(value, issuing)) return false;
  const char* operation = issuing ? "faculty_otp_request" : "faculty_otp_verify";
  const char* challengePath = issuing ? "/api/device/faculty/otp/request/challenge" : "/api/device/faculty/otp/verify/challenge";
  const char* requestPath = issuing ? "/api/device/faculty/otp/request" : "/api/device/faculty/otp/verify";
  String response;
  int code = -1;
  String challengePayload = String("{\"deviceId\":\"") + deviceId + "\"}";
  if (!postFacultyOtpJson(challengePath, challengePayload, response, code, 12000) || code != 200) return false;
  String challengeHex = extractJsonString(response, "challenge");
  uint8_t challenge[EnrollmentAuth::ChallengeSize];
  if (!EnrollmentRecovery::decodeHex(challengeHex.c_str(), challengeHex.length(), challenge, sizeof(challenge))) return false;
  uint8_t key[EnrollmentAuth::KeySize] = {};
  uint8_t requestTag[EnrollmentAuth::TagSize] = {};
  uint8_t digest[32] = {};
  char requestBody[13] = {};
  int requestLength = snprintf(requestBody, sizeof(requestBody), "%s\n", value);
  const mbedtls_md_info_t* sha = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
  bool ok = requestLength > 0 && static_cast<size_t>(requestLength) < sizeof(requestBody) && sha &&
      loadEnrollKey(key, sizeof(key)) &&
      mbedtls_md(sha, reinterpret_cast<const uint8_t*>(requestBody), requestLength, digest) == 0 &&
      EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, operation, challenge, sizeof(challenge),
                                 reinterpret_cast<const uint8_t*>(requestBody), requestLength, requestTag);
  if (ok) {
    char tagText[65], digestText[65];
    EnrollmentAuth::toHex(requestTag, sizeof(requestTag), tagText);
    EnrollmentAuth::toHex(digest, sizeof(digest), digestText);
    String payload = String("{\"deviceId\":\"") + deviceId + "\",\"value\":\"" + value +
                     "\",\"challenge\":\"" + challengeHex + "\",\"tag\":\"" + tagText + "\"}";
    ok = postFacultyOtpJson(requestPath, payload, response, code, issuing ? 65000 : 12000) && code == 200;
    payload = "";
    if (ok) {
      String escapedBody = extractJsonString(response, "body");
      String tagHex = extractJsonString(response, "tag");
      char responseBody[96];
      size_t responseLength = 0;
      uint8_t responseTag[EnrollmentAuth::TagSize];
      ok = EnrollmentRecovery::decodeJsonBody(escapedBody.c_str(), escapedBody.length(),
                                               responseBody, sizeof(responseBody), responseLength) &&
           EnrollmentRecovery::decodeHex(tagHex.c_str(), tagHex.length(), responseTag, sizeof(responseTag)) &&
           FacultyOtp::verifyResponse(issuing, digestText,
             [&](const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength) {
               return EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, operation,
                                                challenge, sizeof(challenge), body, bodyLength, tag, tagLength);
             }, reinterpret_cast<const uint8_t*>(responseBody), responseLength,
             responseTag, sizeof(responseTag), result);
      mbedtls_platform_zeroize(responseBody, sizeof(responseBody));
      mbedtls_platform_zeroize(responseTag, sizeof(responseTag));
    }
  }
  mbedtls_platform_zeroize(requestBody, sizeof(requestBody));
  mbedtls_platform_zeroize(digest, sizeof(digest));
  mbedtls_platform_zeroize(requestTag, sizeof(requestTag));
  mbedtls_platform_zeroize(key, sizeof(key));
  return ok;
}

String extractFacultyResponseBody(const String& json) {
  int start = json.indexOf("\"body\":");
  if (start < 0) return "";
  start += 7;
  while (start < (int)json.length() && (json[start] == ' ' || json[start] == '\t')) ++start;
  if (start >= (int)json.length() || json[start++] != '"') return "";
  for (int i = start; i < (int)json.length(); ++i) {
    if (json[i] == '\\') {
      if (++i >= (int)json.length()) return "";
    } else if (json[i] == '"') {
      return json.substring(start, i);
    }
  }
  return "";
}

#if AC_IR_ENABLED
bool requestAcIr(unsigned operation, const char* command, AcIr::Reply& reply, AcIr::Step& step) {
  if (operation > 2 || !command || !FacultyMenu::hexToken(acIrBoot, strlen(acIrBoot))) return false;
  #if AC_IR_POWER_ENABLED
  const char* purpose = operation == 0 ? "ac_ir_v2_boot" : operation == 1 ? "ac_ir_v2_claim" : "ac_ir_v2_complete";
  const char* challengePath = operation == 0 ? "/api/device/ac-ir/v2/boot/challenge" :
                              operation == 1 ? "/api/device/ac-ir/v2/claim/challenge" : "/api/device/ac-ir/v2/complete/challenge";
  const char* path = operation == 0 ? "/api/device/ac-ir/v2/boot" :
                     operation == 1 ? "/api/device/ac-ir/v2/claim" : "/api/device/ac-ir/v2/complete";
  #else
  const char* purpose = operation == 0 ? "ac_ir_boot" : operation == 1 ? "ac_ir_claim" : "ac_ir_complete";
  const char* challengePath = operation == 0 ? "/api/device/ac-ir/boot/challenge" :
                              operation == 1 ? "/api/device/ac-ir/claim/challenge" : "/api/device/ac-ir/complete/challenge";
  const char* path = operation == 0 ? "/api/device/ac-ir/boot" :
                     operation == 1 ? "/api/device/ac-ir/claim" : "/api/device/ac-ir/complete";
  #endif
  String response;
  int code = -1;
  if (!postFacultyOtpJson(challengePath, String("{\"deviceId\":\"") + deviceId + "\"}", response, code, 12000) || code != 200) return false;
  String challengeHex = extractJsonString(response, "challenge");
  uint8_t challenge[32], key[32] = {}, tag[32] = {}, digest[32] = {};
  if (!EnrollmentRecovery::decodeHex(challengeHex.c_str(), challengeHex.length(), challenge, sizeof(challenge))) return false;
  char requestBody[100] = {};
  int requestLength = snprintf(requestBody, sizeof(requestBody), "%s\n%s\n", acIrBoot, command);
  const mbedtls_md_info_t* sha = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
  bool ok = requestLength > 0 && static_cast<size_t>(requestLength) < sizeof(requestBody) && sha &&
      loadEnrollKey(key, sizeof(key)) &&
      mbedtls_md(sha, reinterpret_cast<const uint8_t*>(requestBody), requestLength, digest) == 0 &&
      EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, purpose, challenge, sizeof(challenge),
                                 reinterpret_cast<const uint8_t*>(requestBody), requestLength, tag);
  if (ok) {
    char tagText[65], digestText[65];
    EnrollmentAuth::toHex(tag, sizeof(tag), tagText);
    EnrollmentAuth::toHex(digest, sizeof(digest), digestText);
    String payload = String("{\"deviceId\":\"") + deviceId + "\",\"boot\":\"" + acIrBoot +
                     "\",\"command\":\"" + command + "\",\"challenge\":\"" + challengeHex +
                     "\",\"tag\":\"" + tagText + "\"}";
    ok = postFacultyOtpJson(path, payload, response, code, 12000) && code == 200;
    payload = "";
    if (ok) {
      String escaped = extractFacultyResponseBody(response);
      String tagHex = extractJsonString(response, "tag");
      char responseBody[192] = {};
      size_t responseLength = 0;
      uint8_t responseTag[32] = {};
      auto verify = [&](const uint8_t* body, size_t bodyLength, const uint8_t* responseMac, size_t macLength) {
        return EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, purpose, challenge, sizeof(challenge),
                                         body, bodyLength, responseMac, macLength);
      };
      ok = FacultyMenu::decodeJsonBody(escaped.c_str(), escaped.length(), responseBody, sizeof(responseBody), responseLength) &&
          EnrollmentRecovery::decodeHex(tagHex.c_str(), tagHex.length(), responseTag, sizeof(responseTag)) &&
          AcIr::parse(operation, digestText, verify, reinterpret_cast<const uint8_t*>(responseBody),
                      responseLength, responseTag, sizeof(responseTag), reply, step, AC_IR_POWER_ENABLED);
      mbedtls_platform_zeroize(responseBody, sizeof(responseBody));
      mbedtls_platform_zeroize(responseTag, sizeof(responseTag));
    }
  }
  mbedtls_platform_zeroize(requestBody, sizeof(requestBody));
  mbedtls_platform_zeroize(digest, sizeof(digest));
  mbedtls_platform_zeroize(tag, sizeof(tag));
  mbedtls_platform_zeroize(key, sizeof(key));
  return ok;
}

void pollAcIr() {
  if (acIrBoot[0] == '\0') {
    uint8_t bootRandom[32];
    for (size_t i = 0; i < sizeof(bootRandom); i += sizeof(uint32_t)) {
      uint32_t word = esp_random();
      memcpy(bootRandom + i, &word, sizeof(word));
    }
    EnrollmentAuth::toHex(bootRandom, sizeof(bootRandom), acIrBoot);
    mbedtls_platform_zeroize(bootRandom, sizeof(bootRandom));
  }
  AcIr::PollOutcome outcome = AcIr::poll(acIrRegistered, acIrHalted,
      [](unsigned operation, const char* command, AcIr::Reply& reply, AcIr::Step& step) {
        return requestAcIr(operation, command, reply, step);
      }, [](bool power, bool increase) {
        acIrSender.sendNEC(power ? 0x08E700FFUL : increase ? 0x08E7A857UL : 0x08E7906FUL, 32, 0);
      });
  switch (outcome) {
    case AcIr::PollOutcome::Registered:
      Serial.println("AC IR: boot registered; administrator reconciliation required.");
      break;
    case AcIr::PollOutcome::BootUnavailable:
      Serial.println("AC IR: boot registration unavailable; no IR sent.");
      break;
    case AcIr::PollOutcome::Sent:
      Serial.println("AC IR: signal emitted; AC reception and setpoint are not verified.");
      break;
    case AcIr::PollOutcome::Halted:
      Serial.println("AC IR: outcome uncertain or unavailable; restart and reconcile before resuming.");
      break;
    default:
      break;
  }
}
#endif

bool requestFacultyMenu(FacultyMenuRequestKind kind, const char* value, unsigned index, FacultyMenu::Result& result) {
  bool login = kind == FacultyMenuRequestKind::Login;
  bool ac = kind == FacultyMenuRequestKind::Ac;
  bool target = kind == FacultyMenuRequestKind::Target;
  if (!isDevicePaired() || (login ? !FacultyOtp::validValue(value, false)
      : !FacultyMenu::hexToken(value, strlen(value))) || (target ? index > 3030 || facultyAcInfo.room == 0 : index > 999)) return false;
  const char* operation = login ? "faculty_menu_login" : ac ? "faculty_menu_ac_v2" : target ? "faculty_menu_ac_target" : "faculty_menu_schedule";
  const char* challengePath = login ? "/api/device/faculty/menu/login/challenge" : ac ? "/api/device/faculty/menu/ac-v2/challenge" : target ? "/api/device/faculty/menu/ac-target/challenge" : "/api/device/faculty/menu/schedule/challenge";
  const char* requestPath = login ? "/api/device/faculty/menu/login" : ac ? "/api/device/faculty/menu/ac-v2" : target ? "/api/device/faculty/menu/ac-target" : "/api/device/faculty/menu/schedule";
  String response;
  int code = -1;
  String challengePayload = String("{\"deviceId\":\"") + deviceId + "\"}";
  if (!postFacultyOtpJson(challengePath, challengePayload, response, code, 12000) || code != 200) return false;
  String challengeHex = extractJsonString(response, "challenge");
  uint8_t challenge[32];
  if (!EnrollmentRecovery::decodeHex(challengeHex.c_str(), challengeHex.length(), challenge, sizeof(challenge))) return false;
  uint8_t key[32] = {}, tag[32] = {}, digest[32] = {};
  char requestBody[96] = {};
  int requestLength = target
      ? snprintf(requestBody, sizeof(requestBody), "%s\n%u\n%llu\n", value, index, static_cast<unsigned long long>(facultyAcInfo.room))
      : snprintf(requestBody, sizeof(requestBody), "%s\n%u\n", value, index);
  const mbedtls_md_info_t* sha = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
  bool ok = requestLength > 0 && static_cast<size_t>(requestLength) < sizeof(requestBody) && sha &&
      loadEnrollKey(key, sizeof(key)) &&
      mbedtls_md(sha, reinterpret_cast<const uint8_t*>(requestBody), requestLength, digest) == 0 &&
      EnrollmentAuth::computeTag(key, sizeof(key), false, deviceId, operation, challenge, sizeof(challenge),
                                 reinterpret_cast<const uint8_t*>(requestBody), requestLength, tag);
  if (ok) {
    char tagText[65], digestText[65];
    EnrollmentAuth::toHex(tag, sizeof(tag), tagText);
    EnrollmentAuth::toHex(digest, sizeof(digest), digestText);
    String payload = String("{\"deviceId\":\"") + deviceId + "\",\"value\":\"" + value +
                     "\",\"index\":" + String(index);
    if (target) {
      char room[21];
      snprintf(room, sizeof(room), "%llu", static_cast<unsigned long long>(facultyAcInfo.room));
      payload += String(",\"roomId\":") + room;
    }
    payload += String(",\"challenge\":\"") + challengeHex +
                     "\",\"tag\":\"" + tagText + "\"}";
    ok = postFacultyOtpJson(requestPath, payload, response, code, 12000) && code == 200;
    payload = "";
    if (ok) {
      String escaped = extractFacultyResponseBody(response);
      String responseTagHex = extractJsonString(response, "tag");
      char responseBody[192] = {};
      size_t responseLength = 0;
      uint8_t responseTag[32] = {};
      auto verify = [&](const uint8_t* body, size_t bodyLength, const uint8_t* responseMac, size_t macLength) {
        return EnrollmentAuth::verifyTag(key, sizeof(key), true, deviceId, operation, challenge, sizeof(challenge),
                                         body, bodyLength, responseMac, macLength);
      };
      ok = FacultyMenu::decodeJsonBody(escaped.c_str(), escaped.length(), responseBody, sizeof(responseBody), responseLength) &&
          EnrollmentRecovery::decodeHex(responseTagHex.c_str(), responseTagHex.length(), responseTag, sizeof(responseTag));
      if (ok) {
        const uint8_t* body = reinterpret_cast<const uint8_t*>(responseBody);
        ok = target ? FacultyMenu::parseTarget(digestText, verify, body, responseLength, responseTag, sizeof(responseTag), facultyTargetResult)
                : ac ? FacultyMenu::parseAc(digestText, verify, body, responseLength, responseTag, sizeof(responseTag), result, facultyAcInfo)
                : FacultyMenu::parse(login, index, digestText, verify, body, responseLength, responseTag, sizeof(responseTag),
                                     result, facultySession, facultySchedule);
      }
      mbedtls_platform_zeroize(responseBody, sizeof(responseBody));
      mbedtls_platform_zeroize(responseTag, sizeof(responseTag));
    }
  }
  mbedtls_platform_zeroize(requestBody, sizeof(requestBody));
  mbedtls_platform_zeroize(digest, sizeof(digest));
  mbedtls_platform_zeroize(tag, sizeof(tag));
  mbedtls_platform_zeroize(key, sizeof(key));
  return ok;
}

void renderFacultySchedule() {
  lcd.clear();
  printLcdRow(0, facultyScheduleDetails ? facultySchedule.room : facultySchedule.subject);
  char times[17];
  snprintf(times, sizeof(times), "%s-%s", facultySchedule.start, facultySchedule.end);
  printLcdRow(1, times);
}

void loadFacultySchedule(unsigned index) {
  lcd.clear();
  printLcdRow(0, "Loading schedule");
  printLcdRow(1, "Please wait");
  FacultyMenu::Result result = FacultyMenu::Result::Denied;
  bool ok = requestFacultyMenu(FacultyMenuRequestKind::Schedule, facultySession, index, result);
  menuActivityAt = millis();
  if (!ok || result == FacultyMenu::Result::Denied) {
    mbedtls_platform_zeroize(facultySession, sizeof(facultySession));
    showMenuNotice("Session ended", "Please sign in", STATE_MENU_FACULTY_ID, true, true);
  } else if (result == FacultyMenu::Result::End) {
    showMenuNotice("No more classes", "Today", STATE_MENU_FACULTY, false, false);
  } else {
    facultyScheduleDetails = false;
    currentDisplayState = STATE_MENU_FACULTY_SCHEDULE;
    renderFacultySchedule();
  }
}

void renderFacultyAc() {
  char line[17];
  snprintf(line, sizeof(line), "Target:%u Min:%u", facultyAcInfo.target, facultyAcInfo.minimum);
  printLcdRow(0, line);
  printLcdRow(1, facultyAcInfo.active ? "*- #+ hold0 back" : "No active class");
}

void adjustFacultyAc(int delta) {
  if (!facultyAcInfo.active) {
    showMenuNotice("No active class", "Cannot adjust", STATE_MENU_FACULTY_AC, false, false);
    return;
  }
  int next = static_cast<int>(facultyAcInfo.target) + delta;
  if (next < 16 || next > 30 || next < static_cast<int>(facultyAcInfo.minimum)) {
    showMenuNotice("Target limit", "Check minimum", STATE_MENU_FACULTY_AC, false, false);
    return;
  }
  lcd.clear();
  printLcdRow(0, "Saving target");
  printLcdRow(1, "Please wait");
  ++statusGeneration;
  FacultyMenu::Result ignored = FacultyMenu::Result::Denied;
  bool ok = requestFacultyMenu(FacultyMenuRequestKind::Target, facultySession,
                               facultyAcInfo.target * 100 + static_cast<unsigned>(next), ignored);
  menuActivityAt = millis();
  if (!ok) {
    showMenuNotice("Outcome unknown", "Reload AC info", STATE_MENU_FACULTY, false, false, 3000);
  } else if (facultyTargetResult == FacultyMenu::TargetResult::Saved) {
    facultyAcInfo.target = static_cast<unsigned>(next);
    showMenuNotice("Target saved", "IR pending", STATE_MENU_FACULTY_AC, false, false, 3000);
  } else if (facultyTargetResult == FacultyMenu::TargetResult::Denied) {
    mbedtls_platform_zeroize(facultySession, sizeof(facultySession));
    showMenuNotice("No room/session", "Please sign in", STATE_MENU_FACULTY_ID, true, true);
  } else {
    const char* notice = facultyTargetResult == FacultyMenu::TargetResult::NoClass ? "No active class" :
                         facultyTargetResult == FacultyMenu::TargetResult::Minimum ? "Minimum changed" : "Target changed";
    showMenuNotice(notice, "Reload AC info", STATE_MENU_FACULTY, false, false, 3000);
  }
}

void loadFacultyAc() {
  lcd.clear();
  printLcdRow(0, "Loading AC info");
  printLcdRow(1, "Please wait");
  FacultyMenu::Result result = FacultyMenu::Result::Denied;
  bool ok = requestFacultyMenu(FacultyMenuRequestKind::Ac, facultySession, 0, result);
  menuActivityAt = millis();
  if (!ok) {
    showMenuNotice("AC unavailable", "Try again later", STATE_MENU_FACULTY, false, false);
  } else if (result == FacultyMenu::Result::Denied) {
    mbedtls_platform_zeroize(facultySession, sizeof(facultySession));
    showMenuNotice("No room/session", "Please sign in", STATE_MENU_FACULTY_ID, true, true);
  } else {
    currentDisplayState = STATE_MENU_FACULTY_AC;
    renderFacultyAc();
  }
}

void submitFacultyPin() {
  if (!FacultyOtp::validValue(entryBuf, false)) {
    showMenuNotice("Need 6 digits", "Then press #", STATE_MENU_FACULTY_PIN, false, false);
    return;
  }
  lcd.clear();
  printLcdRow(0, "Verifying code");
  printLcdRow(1, "Please wait");
  FacultyMenu::Result result = FacultyMenu::Result::Denied;
  mbedtls_platform_zeroize(facultySession, sizeof(facultySession));
  bool ok = requestFacultyMenu(FacultyMenuRequestKind::Login, entryBuf, 0, result);
  clearKeypadEntry();
  menuActivityAt = millis();
  if (!ok) {
    showMenuNotice("Login unavailable", "Try again later", STATE_MENU_FACULTY_ID, true, true);
    return;
  }
  if (result != FacultyMenu::Result::Accepted) {
    denyMenuAuth(STATE_MENU_FACULTY_ID);
    return;
  }
  menuIndex = 0;
  Serial.println("Keypad: faculty authenticated.");
  showMenuNotice("Faculty OK", "", STATE_MENU_FACULTY, false, true);
}

void handleMenuKey(char key) {
  switch (currentDisplayState) {
    case STATE_MENU_UNPAIR_CONFIRM:
      if (key == '#') unpairDeviceLocally();
      else if (key == '*') {
        currentDisplayState = STATE_MENU_ADMIN;
        renderMenu();
      }
      break;
    case STATE_MENU_ROLE:
    case STATE_MENU_ADMIN:
    case STATE_MENU_FACULTY:
    case STATE_MENU_ROOMS:
      if (key == '*') {
        menuMove(-1);
      } else if (key == '#') {
        menuMove(1);
      } else if (key == '0') {
        selectHighlightedItem();
      }
      break;

    case STATE_MENU_FACULTY_SCHEDULE:
      if (key == '*' && facultySchedule.index > 0) loadFacultySchedule(facultySchedule.index - 1);
      else if (key == '#' && facultySchedule.index + 1 < facultySchedule.count && facultySchedule.index < 999)
        loadFacultySchedule(facultySchedule.index + 1);
      else if (key == '0') { facultyScheduleDetails = !facultyScheduleDetails; renderFacultySchedule(); }
      break;

    case STATE_MENU_FACULTY_AC:
      if (key == '*') adjustFacultyAc(-1);
      else if (key == '#') adjustFacultyAc(1);
      else if (key == '0') loadFacultyAc();
      break;

    case STATE_MENU_ADMIN_PIN:
      if (key >= '0' && key <= '9') {
        menuAppendDigit(key);
      } else if (key == '*') {
        menuBackspace();
      } else if (key == '#') {
        submitAdminPin();
      }
      break;

    case STATE_MENU_PAIR_CODE:
      if (key >= '0' && key <= '9') {
        menuAppendDigit(key);
      } else if (key == '*') {
        menuBackspace();
      } else if (key == '#') {
        submitPairCode();
      }
      break;

    case STATE_MENU_FACULTY_ID:
      if (key >= '0' && key <= '9') {
        menuAppendDigit(key);
      } else if (key == '*') {
        menuBackspace();
      } else if (key == '#') {
        submitFacultyId();
      }
      break;

    case STATE_MENU_FACULTY_PIN:
      if (key >= '0' && key <= '9') {
        menuAppendDigit(key);
      } else if (key == '*') {
        menuBackspace();
      } else if (key == '#') {
        submitFacultyPin();
      }
      break;

    default:
      break;
  }
}

void pollKeypad(unsigned long currentMillis) {
  bool activity = keypad.getKeys();

  if (currentDisplayState == STATE_MENU_NOTICE) {
    pickerZeroPending = false;
    bool pressed = false;
    if (activity) {
      for (byte i = 0; i < LIST_MAX; i++) {
        if (keypad.key[i].stateChanged && keypad.key[i].kstate == PRESSED) pressed = true;
      }
    }
    if (pressed || (currentMillis - stateTimer >= menuNoticeDuration)) {
      closeMenuNotice();
    }
    return;
  }

  if (isKeypadMenuState(currentDisplayState)) {
    unsigned long idleLimit = currentDisplayState == STATE_MENU_FACULTY_PIN ? FACULTY_OTP_IDLE_MS : MENU_IDLE_MS;
    if (currentMillis - menuActivityAt >= idleLimit) {
      Serial.println("Keypad menu timed out.");
      enterScanningMode();
      return;
    }
    if (!activity) {
      return;
    }
    for (byte i = 0; i < LIST_MAX; i++) {
      Key& event = keypad.key[i];
      if (!event.stateChanged) continue;

      if (event.kchar == '0' && pickerZeroPending) {
        if (currentDisplayState != pickerZeroState || !isPickerMenu(currentDisplayState)) {
          pickerZeroPending = false;
        } else if (event.kstate == HOLD) {
          pickerZeroPending = false;
          menuActivityAt = currentMillis;
          exitPickerMenu();
        } else if (event.kstate == RELEASED) {
          pickerZeroPending = false;
          menuActivityAt = currentMillis;
          handleMenuKey('0');
        }
        continue;
      }

      if (event.kstate != PRESSED) continue;
      menuActivityAt = currentMillis;
      pickerZeroPending = false;
      if (isPickerMenu(currentDisplayState) && event.kchar == '0') {
        pickerZeroPending = true;
        pickerZeroState = currentDisplayState;
      } else {
        handleMenuKey(event.kchar);
      }
    }
    return;
  }

  // # opens the menu from the ready screen and from short result overlays.
  pickerZeroPending = false;
  if (activity) {
    for (byte i = 0; i < LIST_MAX; i++) {
      if (keypad.key[i].stateChanged && keypad.key[i].kstate == PRESSED && keypad.key[i].kchar == '#') {
        openRoleMenu();
        break;
      }
    }
  }
}

void pollScanAndLog() {
  if (waitForEnrollmentFingerRemoval) {
    if (finger.getImage() == FINGERPRINT_NOFINGER) {
      waitForEnrollmentFingerRemoval = false;
    }
    return;
  }
  if (!attendanceScanEnabled || !isDevicePaired() || isDeviceUnassigned()) {
    fingerDownHandled = false;
    return;
  }

  if (fingerDownHandled) {
    if (finger.getImage() == FINGERPRINT_NOFINGER) {
      fingerDownHandled = false;
    }
    return;
  }

  int fingerprintID = getFingerprintID();
  if (fingerprintID == 0) {
    return;  // no finger
  }

  fingerDownHandled = true;

  if (fingerprintID > 0) {
    if (!serverOnline) {
      // Quick re-check before denying — server may have come back since last poll.
      checkServerHealth(false);
    }
    if (!serverOnline) {
      showDenied("Offline", "Check PC app");
      waitForFingerRemoved();
      fingerDownHandled = false;
      return;
    }

    lcd.clear();
    lcd.setCursor(0, 0);
    lcd.print("Matched ID:");
    lcd.print(fingerprintID);
    lcd.setCursor(0, 1);
    lcd.print("Logging...");

    String responseBody;
    postScan(fingerprintID, responseBody);
    // Success / deny UI is set inside postScan / handleScanResult
  }

  waitForFingerRemoved();
  fingerDownHandled = false;
}

// ---------------------------------------------------------------------------
// Serial: ENROLL | STATUS | HEALTH | GSM | HELP
// Non-blocking: only process complete lines ending in \n / \r.
// Keypad # opens the Admin / Faculty menu (dummy login for now).
// ---------------------------------------------------------------------------
void processSerialCommands() {
  if (provisioningKey && millis() - provisioningStarted >= 10000UL) {
    provisioningKey = false;
    provisioningSerialLocked = true;
    provisioningLength = 0;
    mbedtls_platform_zeroize(provisioningBuffer, sizeof(provisioningBuffer));
    while (Serial.available() > 0) Serial.read();
    Serial.println("SC-KEY-TIMEOUT reboot required");
  }
  while (Serial.available() > 0) {
    if (provisioningSerialLocked) {
      Serial.read();
      continue;
    }
    if (provisioningKey) {
      provisioningBuffer[provisioningLength++] = static_cast<uint8_t>(Serial.read());
      if (provisioningLength == sizeof(provisioningBuffer)) {
        provisioningKey = false;
        uint8_t existing[EnrollmentAuth::KeySize] = {};
        uint8_t tag[EnrollmentAuth::TagSize] = {};
        bool ok = devicePrefs.begin("smartclass", false);
        if (ok) {
          size_t existingLength = devicePrefs.getBytesLength("enrollKey");
          if (existingLength == 0 && !devicePrefs.isKey("enrollKey")) {
            ok = devicePrefs.putBytes("enrollKey", provisioningBuffer, EnrollmentAuth::KeySize) == EnrollmentAuth::KeySize;
          } else {
            ok = existingLength == sizeof(existing) &&
                 devicePrefs.getBytes("enrollKey", existing, sizeof(existing)) == sizeof(existing);
            uint8_t diff = 0;
            if (ok) {
              for (size_t i = 0; i < sizeof(existing); ++i) diff |= existing[i] ^ provisioningBuffer[i];
              ok = diff == 0;
            }
          }
          if (ok) {
            ok = devicePrefs.getBytes("enrollKey", existing, sizeof(existing)) == sizeof(existing) &&
                 EnrollmentAuth::computeTag(existing, sizeof(existing), true, deviceId, "provision",
                    provisioningBuffer + EnrollmentAuth::KeySize, EnrollmentAuth::ChallengeSize,
                    nullptr, 0, tag);
          }
          devicePrefs.end();
        }
        if (ok) {
          char hex[65];
          EnrollmentAuth::toHex(tag, sizeof(tag), hex);
          Serial.printf("SC-KEY-OK %s\n", hex);
          mbedtls_platform_zeroize(hex, sizeof(hex));
        } else {
          Serial.println("SC-KEY-ERROR");
        }
        mbedtls_platform_zeroize(existing, sizeof(existing));
        mbedtls_platform_zeroize(tag, sizeof(tag));
        mbedtls_platform_zeroize(provisioningBuffer, sizeof(provisioningBuffer));
        provisioningLength = 0;
      }
      continue;
    }
    char c = (char)Serial.read();
    if (c == '\r') {
      continue;
    }
    if (c == '\n') {
      if (serialLineLen == 0) {
        continue;
      }
      serialLineBuf[serialLineLen] = '\0';
      String line = String(serialLineBuf);
      serialLineLen = 0;
      line.trim();
      if (line.length() > 0) {
        handleSerialCommand(line);
      }
      continue;
    }

    // Drop non-printable / control noise from USB CDC.
    if (c < 32 || c > 126) {
      continue;
    }

    if (serialLineLen + 1 < sizeof(serialLineBuf)) {
      serialLineBuf[serialLineLen++] = c;
    } else {
      // Overflow — discard line and resync on next newline.
      serialLineLen = 0;
    }
  }
}

void handleSerialCommand(const String& line) {
  if (line.equals("SC-KEY-INFO")) {
    Serial.printf("SC-KEY-INFO 1 %s\n", isDevicePaired() ? deviceId : "unpaired");
    return;
  }
  if (line.equals("SC-KEY-BEGIN")) {
    if (!isDevicePaired() || currentDisplayState != STATE_SCANNING) {
      Serial.println("SC-KEY-ERROR");
      return;
    }
    provisioningLength = 0;
    provisioningStarted = millis();
    provisioningKey = true;
    Serial.println("SC-KEY-READY");
    return;
  }
  if (line.equalsIgnoreCase("HELP")) {
    Serial.printf("SmartClass firmware %s\n", FIRMWARE_VERSION);
    Serial.printf("Commands: ENROLL | DELETEALL | STATUS | HEALTH | GSM | HELP\n");
    Serial.println(ENROLLMENT_WRITES_ENABLED
      ? "  ENROLL     use active admin consent to start one enrollment pass"
      : "  ENROLL     disabled in this build; no enrollment sensor write can run");
    Serial.println("Keypad: # menu | * previous | # next | 0 select | hold 0 back");
    Serial.println("  Faculty login uses SMS OTP; admin PIN is checked by the server.");
    Serial.println("  DELETEALL  wipe all templates on this sensor (+ clear room session)");
    Serial.println("  (Admin can also queue wipe via Devices → clearFingerprints on /status)");
    Serial.println("  GSM        rerun SIM900L modem/SIM/network/signal checks");
    Serial.println("Serial Monitor: set line ending to NL or CR+NL (not \"No line ending\").");
    return;
  }

  if (line.equalsIgnoreCase("HEALTH")) {
    checkServerHealth(true);
    return;
  }

  if (line.equalsIgnoreCase("GSM")) {
    gsmResponding = false;
    gsmSimReady = false;
    gsmNetworkRegistered = false;
    runGsmStartupCheck();
    return;
  }

  if (line.equalsIgnoreCase("STATUS")) {
    Serial.printf("firmware=%s\n", FIRMWARE_VERSION);
    Serial.printf("WiFi=%s", WiFi.status() == WL_CONNECTED ? "connected" : "down");
    if (WiFi.status() == WL_CONNECTED) {
      Serial.print(" IP=");
      Serial.print(WiFi.localIP());
    }
    Serial.println();
    Serial.printf("baseUrl=%s deviceId=%s\n", baseUrl, deviceId);
    Serial.printf("server=%s (GET /health)\n", serverOnline ? "online" : "offline");
    Serial.printf("room=%s\n",
                  !roomStatusReceived ? "(not synced)" :
                  assignedRoomName.length() > 0 ? assignedRoomName.c_str() : "(unassigned)");
    Serial.printf("GSM modem=%s SIM=%s network=%s\n",
                  gsmResponding ? "ready" : "not responding",
                  gsmSimReady ? "ready" : "not ready",
                  gsmNetworkRegistered ? "registered" : "not registered");
    Serial.printf("AC=%s teacher=%s display=%d\n",
                  isGlobalACOn ? "on" : "off",
                  activeTeacherName[0] ? activeTeacherName : "(none)",
                  (int)currentDisplayState);
    printSensorStatus();
    checkServerHealth(true);
    if (serverOnline) {
      Serial.println("Refreshing room status from server...");
      restoreRoomStatusFromServer();
      if (currentDisplayState == STATE_SCANNING) {
        updateScanningDisplay();
      }
    }
    return;
  }

  if (line.equalsIgnoreCase("DELETEALL") || line.equalsIgnoreCase("CLEARFP")) {
    if (currentDisplayState != STATE_SCANNING) {
      Serial.printf("Busy (display state %d) — wait for scanning mode, then try again.\n",
                    (int)currentDisplayState);
      return;
    }
    deleteAllFingerprintsOnSensor();
    return;
  }

  if (line.equalsIgnoreCase("ENROLL")) {
    if (currentDisplayState != STATE_SCANNING) {
      Serial.println("Busy; wait for scanning mode before enrolling.");
      return;
    }
    startEnrollment();
    return;
  }

  // Ignore short USB noise; only complain for deliberate-looking input.
  if (line.length() >= 3) {
    Serial.printf("Unknown command '%s'. Type HELP\n", line.c_str());
  }
}

bool deleteAllFingerprintsOnSensor() {
  if (!enrollmentJournalIsClear()) {
    Serial.println("Sensor clear refused: enrollment journal is pending or unavailable.");
    showDenied("Clear blocked", "Check enrollment");
    return false;
  }
  finger.getTemplateCount();
  uint16_t before = finger.templateCount;
  Serial.printf("DELETEALL: wiping %u sensor template(s)...\n", before);

  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print("Clearing FP...");
  lcd.setCursor(0, 1);
  lcd.print("Please wait");

  uint8_t p = finger.emptyDatabase();
  if (p != FINGERPRINT_OK) {
    Serial.printf("DELETEALL failed: code %u (%s)\n", p, fingerprintErrorName(p));
    showDenied("Clear failed", "Retry DELETEALL");
    return false;
  }

  finger.getTemplateCount();
  Serial.printf("DELETEALL OK: %u -> %u templates on sensor.\n", before, finger.templateCount);

  // Log everyone out of this room: local AC/teacher + server session.
  clearActiveTeacher();
  setAcState(false);
  lastScanWasStart = false;

  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print("AC off + clear");
  lcd.setCursor(0, 1);
  lcd.print("Sync server...");
  bool serverCleared = clearRoomSessionOnServer();
  if (serverCleared) {
    Serial.println("DELETEALL: server room session cleared (AC Off).");
  } else {
    Serial.println("DELETEALL: local AC off; server clear failed or offline.");
  }

  Serial.println("Note: local sensor clear does not remove server fingerprint mappings.");

  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print("Sensor cleared");
  lcd.setCursor(0, 1);
  lcd.print(serverCleared ? "AC OFF synced" : "AC OFF local");
  currentDisplayState = STATE_SHOW_AC;
  stateTimer = millis();
  return true;
}

bool clearRoomSessionOnServer() {
  if (!isDevicePaired()) {
    return false;
  }
  if (WiFi.status() != WL_CONNECTED) {
    serverOnline = false;
    return false;
  }

  // Prefer a fresh health check so we don't skip when last poll was stale.
  checkServerHealth(false);
  if (!serverOnline) {
    return false;
  }

  String responseBody;
  int httpCode = -1;
  String payload = String("{\"deviceId\":\"") + deviceId + "\"}";
  if (!httpPostJson("/api/device/clear-session", payload, responseBody, httpCode)) {
    return false;
  }
  if (httpCode < 200 || httpCode >= 300 || !extractJsonBool(responseBody, "ok", false)) {
    Serial.printf("clear-session HTTP %d: %s\n", httpCode, responseBody.c_str());
    return false;
  }
  return true;
}

// ---------------------------------------------------------------------------
// HTTP JSON helpers (lightweight string parse — no ArduinoJson required)
// ---------------------------------------------------------------------------
String extractJsonString(const String& body, const char* key) {
  String needle = String("\"") + key + "\":";
  int i = body.indexOf(needle);
  if (i < 0) {
    return "";
  }
  i += needle.length();
  while (i < (int)body.length() && (body[i] == ' ' || body[i] == '\t')) {
    i++;
  }
  if (i >= (int)body.length()) {
    return "";
  }
  if (body[i] == '"') {
    i++;
    int end = body.indexOf('"', i);
    if (end < 0) {
      return "";
    }
    return body.substring(i, end);
  }
  // number / bare token until comma or }
  int end = i;
  while (end < (int)body.length() && body[end] != ',' && body[end] != '}' && body[end] != ' ') {
    end++;
  }
  return body.substring(i, end);
}

int extractJsonInt(const String& body, const char* key, int defaultValue) {
  String value = extractJsonString(body, key);
  if (value.length() == 0) {
    return defaultValue;
  }
  return value.toInt();
}

bool extractJsonBool(const String& body, const char* key, bool defaultValue) {
  String v = extractJsonString(body, key);
  if (v.length() == 0) {
    return defaultValue;
  }
  return v.equalsIgnoreCase("true");
}

bool httpGet(const String& path, String& responseBody, int& httpCode) {
  responseBody = "";
  httpCode = -1;
  if (WiFi.status() != WL_CONNECTED) {
    return false;
  }

  String url = String(baseUrl) + path;
  WiFiClient client;
  HTTPClient http;
  http.setConnectTimeout(5000);
  http.setTimeout(8000);
  if (!http.begin(client, url)) {
    Serial.printf("HTTP begin failed for %s\n", url.c_str());
    return false;
  }

  httpCode = http.GET();
  responseBody = http.getString();
  http.end();
  return httpCode > 0;
}

bool checkServerHealth(bool forceLog) {
  ++healthGeneration;
  lastHealthCheckTime = millis();
  if (WiFi.status() != WL_CONNECTED) {
    serverOnline = false;
    if (forceLog) {
      Serial.println("Health: WiFi down — server unreachable.");
    }
    return false;
  }

  String body;
  int httpCode = -1;
  bool reached = httpGet("/health", body, httpCode);
  // ASP.NET MapHealthChecks: Healthy => 200; Degraded/Unhealthy => 503
  bool online = reached && httpCode == 200;
  if (online != serverOnline || forceLog || millis() - lastHealthLogTime > 60000) {
    lastHealthLogTime = millis();
    if (online) {
      Serial.printf("Health: server ONLINE (HTTP %d) %s\n", httpCode, body.c_str());
    } else {
      Serial.printf("Health: server OFFLINE (HTTP %d) — is SmartClass on %s ?\n",
                    httpCode, baseUrl);
    }
  }
  serverOnline = online;
  return serverOnline;
}

bool httpPostJson(const String& path, const String& jsonBody, String& responseBody, int& httpCode, bool touchServerOnline) {
  if (touchServerOnline) ++healthGeneration;
  responseBody = "";
  httpCode = -1;
  if (WiFi.status() != WL_CONNECTED) {
    Serial.println("Offline: WiFi not connected.");
    if (touchServerOnline) {
      serverOnline = false;
    }
    return false;
  }

  String url = String(baseUrl) + path;
  WiFiClient client;
  HTTPClient http;
  http.setConnectTimeout(8000);
  http.setTimeout(12000);
  if (!http.begin(client, url)) {
    Serial.printf("HTTP begin failed for %s\n", url.c_str());
    if (touchServerOnline) {
      serverOnline = false;
    }
    return false;
  }
  http.addHeader("Content-Type", "application/json");
  http.addHeader("X-Device-Api-Key", deviceApiKey);

  Serial.printf("POST %s (WiFi OK, IP %s)\n", url.c_str(), WiFi.localIP().toString().c_str());
  httpCode = http.POST(jsonBody);
  responseBody = http.getString();
  if (httpCode <= 0) {
    if (touchServerOnline) {
      serverOnline = false;
    }
    Serial.printf("HTTP connect/failed code=%d err=%s — is SmartClass listening on 0.0.0.0:5062?\n",
                  httpCode, http.errorToString(httpCode).c_str());
  } else {
    if (touchServerOnline && httpCode >= 200 && httpCode < 500) {
      serverOnline = true;
    }
    Serial.printf("HTTP %d: %s\n", httpCode, responseBody.c_str());
  }
  http.end();
  return httpCode > 0;
}

bool restoreRoomStatusFromServer() {
  return applyRoomStatusFromServer(true);
}

bool applyRoomStatusFromServer(bool announce) {
  ++statusGeneration;
  lastDeviceStatusPollTime = millis();
  if (!isDevicePaired()) {
    roomStatusReceived = false;
    return false;
  }
  String responseBody;
  int httpCode = -1;
  String payload = String("{\"deviceId\":\"") + deviceId + "\"}";
  // Boot/STATUS restore may update reachability; quiet periodic sync must not.
  bool received = httpPostJson("/api/device/status", payload, responseBody, httpCode, announce);
  return applyRoomStatusResponse(announce, received, httpCode, responseBody);
}

bool applyRoomStatusResponse(bool announce, bool received, int httpCode, const String& responseBody) {
  if (!received) {
    roomStatusReceived = false;
    if (announce) {
      Serial.println("Status restore failed: no server reach.");
    } else {
      Serial.println("Status sync skipped/failed (keeping last health result).");
    }
    return false;
  }
  if (httpCode == 401) {
    roomStatusReceived = false;
    Serial.println("Status restore unauthorized — check deviceApiKey.");
    return false;
  }
  if (httpCode < 200 || httpCode >= 300 || !extractJsonBool(responseBody, "ok", false)) {
    roomStatusReceived = false;
    Serial.printf("Status restore HTTP %d: %s\n", httpCode, responseBody.c_str());
    return false;
  }

  bool acOn = extractJsonBool(responseBody, "acOn", false);
  String teacher = extractJsonString(responseBody, "teacherDisplayName");
  String room = extractJsonString(responseBody, "classroomName");
  String classroomId = extractJsonString(responseBody, "classroomId");
  if (classroomId.length() == 0 || (classroomId != "null" && (classroomId.toInt() <= 0 || room.length() == 0))) {
    roomStatusReceived = false;
    Serial.println("Status response has an invalid classroom assignment.");
    return false;
  }
  if (classroomId == "null") {
    room = "";
  }
  bool roomChanged = !roomStatusReceived || assignedRoomName != room;
  assignedRoomName = room;
  roomStatusReceived = true;
  String acStatus = extractJsonString(responseBody, "acStatus");
  String previousDeviceDisplayName = deviceDisplayName;
  int previousTargetTemperature = targetTemperature;
  unsigned long previousStatusPollingInterval = deviceStatusPollingInterval;
  bool previousAttendanceScanEnabled = attendanceScanEnabled;
  bool previousFingerprintEnrollmentEnabled = fingerprintEnrollmentEnabled;
  String nextDeviceDisplayName = extractJsonString(responseBody, "deviceDisplayName");
  if (nextDeviceDisplayName.length() > 0) {
    deviceDisplayName = nextDeviceDisplayName;
  }
  int nextTargetTemperature = extractJsonInt(responseBody, "targetTemperature", targetTemperature);
  if (nextTargetTemperature >= 16 && nextTargetTemperature <= 30) {
    targetTemperature = nextTargetTemperature;
  } else {
    Serial.printf("Ignoring invalid device target temperature: %d\n", nextTargetTemperature);
  }
  int nextPollingSeconds = extractJsonInt(
    responseBody,
    "statusPollingIntervalSeconds",
    (int)(deviceStatusPollingInterval / 1000UL));
  if (nextPollingSeconds >= 5 && nextPollingSeconds <= 3600) {
    deviceStatusPollingInterval = (unsigned long)nextPollingSeconds * 1000UL;
  } else {
    Serial.printf("Ignoring invalid status polling interval: %d seconds\n", nextPollingSeconds);
  }
  attendanceScanEnabled = extractJsonBool(responseBody, "attendanceScanEnabled", attendanceScanEnabled);
  fingerprintEnrollmentEnabled = extractJsonBool(
    responseBody,
    "fingerprintEnrollmentEnabled",
    fingerprintEnrollmentEnabled);
  bool wasOn = isGlobalACOn;
  char previousTeacher[sizeof(activeTeacherName)];
  strncpy(previousTeacher, activeTeacherName, sizeof(previousTeacher));
  previousTeacher[sizeof(previousTeacher) - 1] = '\0';

  if (acOn) {
    rememberActiveTeacher(teacher);
    setAcState(true);
  } else {
    setAcState(false);
  }

  bool teacherChanged = acOn && strncmp(previousTeacher, activeTeacherName, sizeof(activeTeacherName)) != 0;
  bool settingsChanged =
    previousDeviceDisplayName != deviceDisplayName ||
    previousTargetTemperature != targetTemperature ||
    previousStatusPollingInterval != deviceStatusPollingInterval ||
    previousAttendanceScanEnabled != attendanceScanEnabled ||
    previousFingerprintEnrollmentEnabled != fingerprintEnrollmentEnabled;
  bool changed = (acOn != wasOn) || teacherChanged || settingsChanged || roomChanged;

  if (announce && !isKeypadMenuState(currentDisplayState)) {
    lcd.clear();
    lcd.setCursor(0, 0);
    if (acOn) {
      lcd.print("Restored AC ON");
      lcd.setCursor(0, 1);
      if (activeTeacherName[0] != '\0') {
        lcd.print(activeTeacherName);
      } else if (room.length() > 0) {
        lcd.print(room.substring(0, 16));
      } else {
        lcd.print(acStatus.substring(0, 16));
      }
    } else {
      lcd.print("Restored AC OFF");
      lcd.setCursor(0, 1);
      if (room.length() > 0) {
        lcd.print(room.substring(0, 16));
      } else {
        lcd.print("Unassigned");
      }
    }
  } else if (changed && !announce) {
    Serial.printf("Device status sync: acOn=%d status=%s\n", acOn ? 1 : 0, acStatus.c_str());
    if (currentDisplayState == STATE_SCANNING) {
      updateScanningDisplay();
    }
  }

  bool clearFingerprints = extractJsonBool(responseBody, "clearFingerprints", false);
  if (clearFingerprints) {
    if (currentDisplayState == STATE_SCANNING || currentDisplayState == STATE_SHOW_AC) {
      Serial.println("Remote clearFingerprints requested.");
      deleteAllFingerprintsOnSensor();
    } else {
      Serial.printf("Remote clearFingerprints pending (busy state %d) — retry next poll.\n",
                    (int)currentDisplayState);
    }
  }

  if (announce || changed) {
    Serial.printf("Status %s: device=%s room=%s acOn=%d target=%dC poll=%lus scan=%d enroll=%d teacher=%s session=%d clearFP=%d\n",
                  announce ? "restore" : "sync",
                  deviceDisplayName.c_str(),
                  room.c_str(),
                  acOn ? 1 : 0,
                  targetTemperature,
                  deviceStatusPollingInterval / 1000UL,
                  attendanceScanEnabled ? 1 : 0,
                  fingerprintEnrollmentEnabled ? 1 : 0,
                  activeTeacherName[0] ? activeTeacherName : "(none)",
                  extractJsonBool(responseBody, "sessionActive", false) ? 1 : 0,
                  clearFingerprints ? 1 : 0);
  }
  return true;
}

bool postScan(int templateId, String& responseBody) {
  ++statusGeneration;
  int httpCode = -1;
  String payload = String("{\"templateId\":") + String(templateId) +
                   ",\"deviceId\":\"" + deviceId + "\"}";
  if (!httpPostJson("/api/device/fingerprint/scan", payload, responseBody, httpCode)) {
    showDenied("No reach PC", "Bind 0.0.0.0");
    Serial.printf("Scan POST failed (httpCode=%d). WiFi is up but %s is unreachable.\n", httpCode, baseUrl);
    Serial.println("Fix: run SmartClass with ASPNETCORE_URLS=http://0.0.0.0:5062 (not localhost-only).");
    Serial.println("Also confirm baseUrl IP matches the PC (ipconfig) and Windows Firewall allows port 5062.");
    return false;
  }
  handleScanResult(templateId, responseBody, httpCode);
  return true;
}

void handleScanResult(int templateId, const String& body, int httpCode) {
  String action = extractJsonString(body, "action");
  String message = extractJsonString(body, "message");
  auto nameResult = ScanDisplayName::read(body.c_str(), scannedTeacherName);
  if (nameResult == ScanDisplayName::Result::InvalidResponse)
    Serial.println("Scan response could not be parsed for teacher name.");
  if (message.length() == 0) {
    message = extractJsonString(body, "error");
  }
  Serial.printf("Scan templateId=%d action=%s http=%d msg=%s\n",
                templateId, action.c_str(), httpCode, message.c_str());

  if (httpCode == 401 || action == "unauthorized") {
    showDenied("Unauthorized", "Check API key");
    return;
  }

  if (action == "unassigned") {
    showDenied("Unassigned", "Ask administrator");
    return;
  }

  if (action == "disabled") {
    showDenied("Scan disabled", "Ask administrator");
    return;
  }

  if (action == "started") {
    rememberActiveTeacher(scannedTeacherName);
    setAcState(true);
    lastScanWasStart = true;
    char matchedLine[17];
    snprintf(matchedLine, sizeof(matchedLine), "Matched ID:%d", templateId);
    showScanResult(matchedLine, activeTeacherName[0] ? activeTeacherName : "Class started", STATE_SHOW_MATCH);
    return;
  }

  if (action == "ac_on") {
    rememberActiveTeacher(scannedTeacherName);
    setAcState(true);
    lastScanWasStart = true;
    showScanResult("AC resumed", activeTeacherName[0] ? activeTeacherName : "In window", STATE_SHOW_MATCH);
    return;
  }

  if (action == "ended") {
    clearActiveTeacher();
    setAcState(false);
    lastScanWasStart = false;
    showScanResult("Class ended", scannedTeacherName[0] ? scannedTeacherName : "Ready to scan", STATE_SHOW_MATCH);
    return;
  }

  if (action == "ac_off") {
    clearActiveTeacher();
    setAcState(false);
    lastScanWasStart = false;
    showScanResult("AC off", scannedTeacherName[0] ? scannedTeacherName : "Ready to scan", STATE_SHOW_MATCH);
    return;
  }

  if (action == "unknown") {
    showDenied("Unknown finger", "Enroll on device");
    return;
  }

  if (action == "unknown_device") {
    showDenied("Bad deviceId", deviceId);
    return;
  }

  if (action == "wrong_room") {
    showScanResult("Wrong room", "Use class room", STATE_SHOW_DENIED);
    return;
  }

  if (action == "no_schedule") {
    showScanResult("No schedule", "Not in window", STATE_SHOW_DENIED);
    return;
  }

  if (action == "error" || httpCode >= 500) {
    String line1 = message.length() > 0 ? message.substring(0, 16) : "Server exception";
    showScanResult("Server error", line1.c_str(), STATE_SHOW_DENIED);
    return;
  }

  // Unexpected / empty action (e.g. HTML error page)
  char httpLine[17];
  snprintf(httpLine, sizeof(httpLine), "HTTP %d", httpCode);
  showDenied(httpLine, action.length() ? action.c_str() : "Bad response");
}

void setAcState(bool on) {
  if (on && !isGlobalACOn) {
    acStartTime = millis();
  } else if (!on && isGlobalACOn) {
    acStartTime = 0;
  }
  isGlobalACOn = on;
  if (!on) {
    clearActiveTeacher();
  }
  applyAcRelay();
}

void rememberActiveTeacher(const String& name) {
  String trimmed = name;
  trimmed.trim();
  if (trimmed.length() == 0) {
    strncpy(activeTeacherName, "Teacher", sizeof(activeTeacherName) - 1);
  } else {
    strncpy(activeTeacherName, trimmed.c_str(), sizeof(activeTeacherName) - 1);
  }
  activeTeacherName[sizeof(activeTeacherName) - 1] = '\0';
}

void clearActiveTeacher() {
  activeTeacherName[0] = '\0';
}

void applyAcRelay() {
  if (AC_RELAY_PIN < 0) {
    return;
  }
  bool level = isGlobalACOn ? (AC_RELAY_ACTIVE_HIGH != 0) : (AC_RELAY_ACTIVE_HIGH == 0);
  digitalWrite(AC_RELAY_PIN, level ? HIGH : LOW);
}

void showDenied(const char* line0, const char* line1) {
  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print(line0);
  lcd.setCursor(0, 1);
  lcd.print(line1);
  currentDisplayState = STATE_SHOW_DENIED;
  stateTimer = millis();
}

void showScanResult(const char* line0, const char* line1, DisplayState resultState) {
  pendingScanNotice = ScanResultNotice::make(scannedTeacherName, line0, line1);
  pendingScanResultState = resultState;
  const auto& first = ScanResultNotice::first(pendingScanNotice);
  lcd.clear();
  printLcdRow(0, first.line0);
  printLcdRow(1, first.line1);
  currentDisplayState = pendingScanNotice.hasName ? STATE_SHOW_SCAN_NAME : resultState;
  stateTimer = millis();
}

void manageDisplayState(unsigned long currentMillis) {
  switch (currentDisplayState) {
    case STATE_SHOW_SCAN_NAME:
      if (currentMillis - stateTimer >= ScanResultNotice::NameDurationMs) {
        lcd.clear();
        printLcdRow(0, pendingScanNotice.result.line0);
        printLcdRow(1, pendingScanNotice.result.line1);
        currentDisplayState = pendingScanResultState;
        stateTimer = currentMillis;
      }
      break;

    case STATE_SHOW_MATCH:
      if (currentMillis - stateTimer >= 1000) {
        lcd.clear();
        lcd.setCursor(0, 0);
        if (lastScanWasStart) {
          lcd.print("AC turned ON ");
          lcd.write(0);
          lcd.setCursor(0, 1);
          if (activeTeacherName[0] != '\0') {
            lcd.print(activeTeacherName);
          } else {
            lcd.print("In session");
          }
        } else {
          lcd.print("AC turned OFF ");
          lcd.write(1);
          lcd.setCursor(0, 1);
          lcd.print(scannedTeacherName[0] ? scannedTeacherName : "Ready to scan");
        }
        currentDisplayState = STATE_SHOW_AC;
        stateTimer = currentMillis;
      }
      break;

    case STATE_SHOW_AC:
      if (currentMillis - stateTimer >= 1500) {
        enterScanningMode();
      }
      break;

    case STATE_SHOW_DENIED:
      if (currentMillis - stateTimer >= 2000) {
        enterScanningMode();
      }
      break;

    case STATE_ENROLL:
    case STATE_SCANNING:
    default:
      break;
  }
}

void updateScanningDisplay() {
  struct tm timeinfo;
  float t = dht.readTemperature();

  char timeStr[6] = "--:--";
  char elapsedStr[6] = "00:00";
  char tempStr[4] = "--C";
  char colonChar = showColon ? ':' : ' ';

  if (getLocalTime(&timeinfo) && timeinfo.tm_year > 70) {
    snprintf(timeStr, sizeof(timeStr), "%02d%c%02d",
             timeinfo.tm_hour, colonChar, timeinfo.tm_min);
  }

  if (isGlobalACOn && acStartTime != 0) {
    unsigned long elapsedSec = (millis() - acStartTime) / 1000UL;
    unsigned long eh = (elapsedSec / 3600UL) % 100UL;
    unsigned long em = (elapsedSec / 60UL) % 60UL;
    snprintf(elapsedStr, sizeof(elapsedStr), "%02lu%c%02lu", eh, colonChar, em);
  }

  if (!isnan(t)) {
    int whole = (int)(t + 0.5f);
    if (whole < 0) {
      whole = 0;
    }
    if (whole > 99) {
      whole = 99;
    }
    snprintf(tempStr, sizeof(tempStr), "%02dC", whole);
  }

  bool showRoom = roomStatusReceived && WiFi.status() == WL_CONNECTED && serverOnline &&
                  (millis() / 4000UL) % 2 == 1;
  if (isGlobalACOn) {
    const char* name = activeTeacherName[0] ? activeTeacherName : "Teacher";
    printLcdRow(0, showRoom ? (assignedRoomName.length() > 0 ? assignedRoomName.c_str() : "Unassigned") : name);

    // Row 2: HH:MM  HH:MM  TTC + AC icon  → 16 chars
    //         clock  elapsed temp   icon
    char line1[17];
    snprintf(line1, sizeof(line1), "%s %s %s ", timeStr, elapsedStr, tempStr);
    lcd.setCursor(0, 1);
    lcd.print(line1);
    lcd.setCursor(15, 1);
    lcd.write((uint8_t)0);
  } else {
    // Row 1: ready prompt; row 2: clock/temp, or Offline when unreachable
    printLcdRow(0, !isDevicePaired() ? "Not paired" : (showRoom || isDeviceUnassigned())
      ? (assignedRoomName.length() > 0 ? assignedRoomName.c_str() : "Unassigned")
      : (attendanceScanEnabled ? "Ready to scan" : "Scan disabled"));
    if (WiFi.status() != WL_CONNECTED || !serverOnline) {
      lcd.setCursor(0, 1);
      lcd.print("Offline         ");
    } else {
      char line1[17];
      snprintf(line1, sizeof(line1), "%-5s %-3s T%02d  ", timeStr, tempStr, targetTemperature);
      lcd.setCursor(0, 1);
      lcd.print(line1);
      lcd.setCursor(15, 1);
      lcd.write((uint8_t)1);
    }
  }
}

void printSensorStatus() {
  finger.getTemplateCount();
  Serial.printf("Sensor templates stored: %u (capacity %u)\n",
                finger.templateCount, finger.capacity);
  if (finger.templateCount == 0) {
    Serial.println("WARNING: sensor has 0 templates. Fingerprint enrollment is not currently available.");
  }
}

const char* fingerprintErrorName(uint8_t code) {
  switch (code) {
    case FINGERPRINT_OK: return "OK";
    case FINGERPRINT_NOFINGER: return "NOFINGER";
    case FINGERPRINT_PACKETRECIEVEERR: return "PACKET_ERR";
    case FINGERPRINT_IMAGEFAIL: return "IMAGEFAIL";
    case FINGERPRINT_IMAGEMESS: return "IMAGEMESS";
    case FINGERPRINT_FEATUREFAIL: return "FEATUREFAIL";
    case FINGERPRINT_NOTFOUND: return "NOTFOUND";
    default: return "OTHER";
  }
}

void waitForFingerRemoved() {
  unsigned long start = millis();
  while (finger.getImage() != FINGERPRINT_NOFINGER && millis() - start < 5000) {
    delay(50);
  }
  delay(200);
}

// Returns:
//   >0  matched template id
//    0  no finger present
//   <0  finger was present but match/capture failed (UI already updated)
int getFingerprintID() {
  uint8_t p = finger.getImage();
  if (p == FINGERPRINT_NOFINGER) {
    return 0;
  }
  if (p != FINGERPRINT_OK) {
    // Throttle: PACKET_ERR spam on a loose wire floods Serial and looks like "boxes"
    // when mixed with binary. Log at most every 2s.
    static unsigned long lastImgErrLog = 0;
    if (millis() - lastImgErrLog > 2000) {
      lastImgErrLog = millis();
      Serial.printf("getImage failed: %s (%u)\n", fingerprintErrorName(p), p);
    }
    // Soft image errors: ask user to retry without locking the UI for long.
    if (p == FINGERPRINT_IMAGEFAIL || p == FINGERPRINT_IMAGEMESS || p == FINGERPRINT_PACKETRECIEVEERR) {
      showDenied("Scan unclear", "Press firmly");
      return -1;
    }
    return -1;
  }

  p = finger.image2Tz();
  if (p != FINGERPRINT_OK) {
    Serial.printf("image2Tz failed: %s (%u)\n", fingerprintErrorName(p), p);
    showDenied("Scan unclear", "Try again");
    return -2;
  }

  finger.getTemplateCount();
  if (finger.templateCount == 0) {
    Serial.println("Local sensor: 0 templates — enroll on this device first.");
    showDenied("No enrollments", "Ask administrator");
    return -3;
  }

  p = finger.fingerFastSearch();
  if (p == FINGERPRINT_PACKETRECIEVEERR) {
    // Some modules are happier with the classic search.
    p = finger.fingerSearch();
  }

  if (p == FINGERPRINT_NOTFOUND) {
    Serial.println("Local sensor: no match for this finger.");
    showDenied("Access Denied!", "Unknown finger");
    return -4;
  }
  if (p != FINGERPRINT_OK) {
    Serial.printf("fingerSearch failed: %s (%u)\n", fingerprintErrorName(p), p);
    showDenied("Sensor error", fingerprintErrorName(p));
    return -5;
  }

  Serial.print("Found ID #");
  Serial.print(finger.fingerID);
  Serial.print(" confidence ");
  Serial.println(finger.confidence);
  return finger.fingerID;
}
