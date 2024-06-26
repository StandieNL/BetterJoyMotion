using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Threading;
using System.Windows.Forms;
using BetterJoyForCemu.Controller;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System.Threading.Tasks;


namespace BetterJoyForCemu {
    public class Joycon {
        public string path = String.Empty;
        public bool isPro = false;
        public bool isSnes = false;
        bool isUSB = false;
        private Joycon _other = null;
        public Joycon other {
            get {
                return _other;
            }
            set {
                _other = value;

                // If the other Joycon is itself, the Joycon is sideways
                if (_other == null || _other == this) {
                    // Set LED to current Pad ID
                    SetLEDByPlayerNum(PadId);
                } else {
                    // Set LED to current Joycon Pair
                    int lowestPadId = Math.Min(_other.PadId, PadId);
                    SetLEDByPlayerNum(lowestPadId);
                }
            }
        }
        public bool active_gyro = false;

        private long inactivity = Stopwatch.GetTimestamp();

        public bool send = true;

        public enum Direction {
            POS,
            NEG,
            ANY
        }
        public enum Range {
            OFF,
            ON
        }

        public enum DebugType : int {
            NONE,
            ALL,
            COMMS,
            THREADING,
            IMU,
            RUMBLE,
            SHAKE,
        };
        public DebugType debug_type = (DebugType)int.Parse(ConfigurationManager.AppSettings["DebugType"]);
        //public DebugType debug_type = DebugType.NONE; //Keep this for manual debugging during development.
        public bool isLeft;
        public enum state_ : uint {
            NOT_ATTACHED,
            DROPPED,
            NO_JOYCONS,
            ATTACHED,
            INPUT_MODE_0x30,
            IMU_DATA_OK,
        };
        public state_ state;
        public enum Button : int {
            DPAD_DOWN = 0,
            DPAD_RIGHT = 1,
            DPAD_LEFT = 2,
            DPAD_UP = 3,
            SL = 4,
            SR = 5,
            MINUS = 6,
            HOME = 7,
            PLUS = 8,
            CAPTURE = 9,
            STICK = 10,
            SHOULDER_1 = 11,
            SHOULDER_2 = 12,

            // For pro controller
            B = 13,
            A = 14,
            Y = 15,
            X = 16,
            STICK2 = 17,
            SHOULDER2_1 = 18,
            SHOULDER2_2 = 19,
        };
        private bool[] buttons_down = new bool[20];
        private bool[] buttons_up = new bool[20];
        private bool[] buttons = new bool[20];
        private bool[] down_ = new bool[20];
        private long[] buttons_down_timestamp = new long[20];

        private float[] stick = { 0, 0 };
        private float[] stick2 = { 0, 0 };

        // Configuration class for Shake Detection
        public class ShakeConfig {
            public float AccelShakeSensitivityX;
            public float AccelShakeSensitivityY;
            public float AccelShakeSensitivityZ;
            public float GyroShakeSensitivityX; // Roll
            public float GyroShakeSensitivityY; // Vertical shake
            public float GyroShakeSensitivityZ; // Horizontal shake
            public long ShakeDelay;
            public Button SimulatedButton; // The button to simulate when a shake is detected

            // Constructor
            public ShakeConfig(float accelX, float accelY, float accelZ, float gyroX, float gyroY, float gyroZ, long delay, Button button) {
                AccelShakeSensitivityX = accelX;
                AccelShakeSensitivityY = accelY;
                AccelShakeSensitivityZ = accelZ;
                GyroShakeSensitivityX = gyroX;
                GyroShakeSensitivityY = gyroY;
                GyroShakeSensitivityZ = gyroZ;
                ShakeDelay = delay;
                SimulatedButton = button;
            }
        }

        public static Dictionary<int, ShakeConfig> ShakeConfigs = new Dictionary<int, ShakeConfig>();

        private IntPtr handle;

        byte[] default_buf = { 0x0, 0x1, 0x40, 0x40, 0x0, 0x1, 0x40, 0x40 };

        private byte[] stick_raw = { 0, 0, 0 };
        private UInt16[] stick_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone;
        private UInt16[] stick_precal = { 0, 0 };

        private byte[] stick2_raw = { 0, 0, 0 };
        private UInt16[] stick2_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone2;
        private UInt16[] stick2_precal = { 0, 0 };

        private bool stop_polling = true;
        private bool imu_enabled = false;
        private Int16[] acc_r = { 0, 0, 0 };
        private Int16[] acc_neutral = { 0, 0, 0 };
        private Int16[] acc_sensiti = { 0, 0, 0 };
        private Vector3 acc_g;

        private Int16[] gyr_r = { 0, 0, 0 };
        private Int16[] gyr_neutral = { 0, 0, 0 };
        private Int16[] gyr_sensiti = { 0, 0, 0 };
        private Vector3 gyr_g;

        private float[] cur_rotation; // Filtered IMU data

        private short[] acc_sen = new short[3]{
            16384,
            16384,
            16384
        };
        private short[] gyr_sen = new short[3]{
            18642,
            18642,
            18642
        };

        private Int16[] pro_hor_offset = { -710, 0, 0 };
        private Int16[] left_hor_offset = { 0, 0, 0 };
        private Int16[] right_hor_offset = { 0, 0, 0 };

        private bool do_localize;
        private float filterweight;
        private const uint report_len = 49;

        private struct Rumble {
            public Queue<float[]> queue;

            public void set_vals(float low_freq, float high_freq, float amplitude) {
                float[] rumbleQueue = new float[] { low_freq, high_freq, amplitude };
                // Keep a queue of 15 items, discard oldest item if queue is full.
                if (queue.Count > 15) {
                    queue.Dequeue();
                }
                queue.Enqueue(rumbleQueue);
            }
            public Rumble(float[] rumble_info) {
                queue = new Queue<float[]>();
                queue.Enqueue(rumble_info);
            }
            private float clamp(float x, float min, float max) {
                if (x < min) return min;
                if (x > max) return max;
                return x;
            }

            private byte EncodeAmp(float amp) {
                byte en_amp;

                if (amp == 0)
                    en_amp = 0;
                else if (amp < 0.117)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) / (5 - Math.Pow(amp, 2)) - 1);
                else if (amp < 0.23)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) - 0x5c);
                else
                    en_amp = (byte)((((Math.Log(amp * 1000, 2) * 32) - 0x60) * 2) - 0xf6);

                return en_amp;
            }

            public byte[] GetData() {
                byte[] rumble_data = new byte[8];
                float[] queued_data = queue.Dequeue();

                if (queued_data[2] == 0.0f) {
                    rumble_data[0] = 0x0;
                    rumble_data[1] = 0x1;
                    rumble_data[2] = 0x40;
                    rumble_data[3] = 0x40;
                } else {
                    queued_data[0] = clamp(queued_data[0], 40.875885f, 626.286133f);
                    queued_data[1] = clamp(queued_data[1], 81.75177f, 1252.572266f);

                    queued_data[2] = clamp(queued_data[2], 0.0f, 1.0f);

                    UInt16 hf = (UInt16)((Math.Round(32f * Math.Log(queued_data[1] * 0.1f, 2)) - 0x60) * 4);
                    byte lf = (byte)(Math.Round(32f * Math.Log(queued_data[0] * 0.1f, 2)) - 0x40);
                    byte hf_amp = EncodeAmp(queued_data[2]);

                    UInt16 lf_amp = (UInt16)(Math.Round((double)hf_amp) * .5);
                    byte parity = (byte)(lf_amp % 2);
                    if (parity > 0) {
                        --lf_amp;
                    }

                    lf_amp = (UInt16)(lf_amp >> 1);
                    lf_amp += 0x40;
                    if (parity > 0) lf_amp |= 0x8000;

                    hf_amp = (byte)(hf_amp - (hf_amp % 2)); // make even at all times to prevent weird hum
                    rumble_data[0] = (byte)(hf & 0xff);
                    rumble_data[1] = (byte)(((hf >> 8) & 0xff) + hf_amp);
                    rumble_data[2] = (byte)(((lf_amp >> 8) & 0xff) + lf);
                    rumble_data[3] = (byte)(lf_amp & 0xff);
                }

                for (int i = 0; i < 4; ++i) {
                    rumble_data[4 + i] = rumble_data[i];
                }

                return rumble_data;
            }
        }

        private Rumble rumble_obj;

        private byte global_count = 0;
        private string debug_str;

        // For UdpServer
        public int PadId = 0;
        public int battery = -1;
        public int model = 2;
        public int constate = 2;
        public int connection = 3;

        public PhysicalAddress PadMacAddress = new PhysicalAddress(new byte[] { 01, 02, 03, 04, 05, 06 });
        public ulong Timestamp = 0;
        public int packetCounter = 0;

        public OutputControllerXbox360 out_xbox;
        public OutputControllerDualShock4 out_ds4;
        ushort ds4_ts = 0;
        ulong lag;

        int lowFreq = Int32.Parse(ConfigurationManager.AppSettings["LowFreqRumble"]);
        int highFreq = Int32.Parse(ConfigurationManager.AppSettings["HighFreqRumble"]);

        bool toRumble = Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]);

        bool showAsXInput = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]);
        bool showAsDS4 = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"]);

        public MainForm form;

        public byte LED { get; private set; } = 0x0;
        public void SetLEDByPlayerNum(int id) {
            if (id > 3) {
                // No support for any higher than 3 (4 Joycons/Controllers supported in the application normally)
                id = 3;
            }

            if (ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings["UseIncrementalLights"].Value.ToLower() == "true") {
                // Set all LEDs from 0 to the given id to lit
                int ledId = id;
                LED = 0x0;
                do {
                    LED |= (byte)(0x1 << ledId);
                } while (--ledId >= 0);
            } else {
                LED = (byte)(0x1 << id);
            }

            SetPlayerLED(LED);
        }

        public string serial_number;
        bool thirdParty = false;

        private float[] activeData;
        static float AHRS_beta = float.Parse(ConfigurationManager.AppSettings["AHRS_beta"]);
        private MadgwickAHRS AHRS = new MadgwickAHRS(0.005f, AHRS_beta); // for getting filtered Euler angles of rotation; 5ms sampling rate

        public Joycon(IntPtr handle_, bool imu, bool localize, float alpha, bool left, string path, string serialNum, int id = 0, bool isPro = false, bool isSnes = false, bool thirdParty = false) {
            serial_number = serialNum;
            activeData = new float[6];
            handle = handle_;
            imu_enabled = imu;
            do_localize = localize;
            rumble_obj = new Rumble(new float[] { lowFreq, highFreq, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            filterweight = alpha;
            isLeft = left;

            PadId = id;
            LED = (byte)(0x1 << PadId);
            this.isPro = isPro || isSnes;
            this.isSnes = isSnes;
            isUSB = serialNum == "000000000001";
            this.thirdParty = thirdParty;

            this.path = path;

            connection = isUSB ? 0x01 : 0x02;

            if (showAsXInput) {
                out_xbox = new OutputControllerXbox360();
                if (toRumble)
                    out_xbox.FeedbackReceived += ReceiveRumble;
            }

            if (showAsDS4) {
                out_ds4 = new OutputControllerDualShock4();
                if (toRumble)
                    out_ds4.FeedbackReceived += Ds4_FeedbackReceived;
            }
        }

        bool IsButtonPressed(Button button) {
            return buttons[(int)button];
        }

        public enum State {
            OFF = 0,
            ON = 1
        }
        public enum ButtonFunctionality {
            OFF,
            SL, //Represents SL left joy-con: buttons[(int)Button.SL]
            SR, // Represents SL left joy-con: buttons[(int)Button.SL]
            SLO, // Represents SL right joy-con: other.buttons[(int)Button.SL]
            SRO  // Represents SR right joy-con: other.buttons[(int)Button.SR]
        }
        public enum TriggerFunctionality {
            OFF,
            SL, //Represents SL left joy-con: buttons[(int)Button.SL]
            SR, // Represents SL left joy-con: buttons[(int)Button.SL]
            SLO, // Represents SL right joy-con: other.buttons[(int)Button.SL]
            SRO  // Represents SR right joy-con: other.buttons[(int)Button.SR]
        }
        public enum Mutate {
            OFF,
            SL,
            SR,
            DPAD_DOWN,
            DPAD_RIGHT,
            DPAD_LEFT,
            DPAD_UP,
            A,
            B,
            X,
            Y,
            MINUS,
            HOME,
            PLUS,
            CAPTURE,
            STICK,
            STICK2,
            SHOULDER_1,
            SHOULDER2_1,
            SHOULDER_2,
            SHOULDER2_2,
            ON
        }


        public void getActiveData() {
            this.activeData = form.activeCaliData(serial_number);
        }

        public void ReceiveRumble(Xbox360FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Recived: XInput", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void Ds4_FeedbackReceived(DualShock4FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Recived: DS4", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void DebugPrint(String s, DebugType d) {
            if (debug_type == DebugType.NONE) return;
            if (d == DebugType.ALL || d == debug_type || debug_type == DebugType.ALL) {
                form.AppendTextBox(s + "\r\n");
            }
        }
        public bool GetButtonDown(Button b) {
            return buttons_down[(int)b];
        }
        public bool GetButton(Button b) {
            return buttons[(int)b];
        }
        public bool GetButtonUp(Button b) {
            return buttons_up[(int)b];
        }
        public float[] GetStick() {
            return stick;
        }
        public float[] GetStick2() {
            return stick2;
        }
        public Vector3 GetGyro() {
            return gyr_g;
        }
        public Vector3 GetAccel() {
            return acc_g;
        }
        public int Attach() {
            state = state_.ATTACHED;

            // Make sure command is received
            HIDapi.hid_set_nonblocking(handle, 0);

            byte[] a = { 0x0 };

            // Connect
            if (isUSB) {
                a = Enumerable.Repeat((byte)0, 64).ToArray();
                form.AppendTextBox("Using USB.\r\n");

                a[0] = 0x80;
                a[1] = 0x1;
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                if (a[0] != 0x81) { // can occur when USB connection isn't closed properly
                    form.AppendTextBox("Resetting USB connection.\r\n");
                    Subcommand(0x06, new byte[] { 0x01 }, 1);
                    throw new Exception("reset_usb");
                }

                if (a[3] == 0x3) {
                    PadMacAddress = new PhysicalAddress(new byte[] { a[9], a[8], a[7], a[6], a[5], a[4] });
                }

                // USB Pairing
                a = Enumerable.Repeat((byte)0, 64).ToArray();
                a[0] = 0x80; a[1] = 0x2; // Handshake
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x3; // 3Mbit baud rate
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x2; // Handshake at new baud rate
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x4; // Prevent HID timeout
                HIDapi.hid_write(handle, a, new UIntPtr(2)); // doesn't actually prevent timout...
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

            }
            dump_calibration_data();

            // Bluetooth manual pairing
            byte[] btmac_host = Program.btMAC.GetAddressBytes();
            // send host MAC and acquire Joycon MAC
            //byte[] reply = Subcommand(0x01, new byte[] { 0x01, btmac_host[5], btmac_host[4], btmac_host[3], btmac_host[2], btmac_host[1], btmac_host[0] }, 7, true);
            //byte[] LTKhash = Subcommand(0x01, new byte[] { 0x02 }, 1, true);
            // save pairing info
            //Subcommand(0x01, new byte[] { 0x03 }, 1, true);

            BlinkHomeLight();
            SetLEDByPlayerNum(PadId);

            Subcommand(0x40, new byte[] { (imu_enabled ? (byte)0x1 : (byte)0x0) }, 1);
            Subcommand(0x48, new byte[] { 0x01 }, 1);

            Subcommand(0x3, new byte[] { 0x30 }, 1);
            DebugPrint("Done with init.", DebugType.COMMS);

            HIDapi.hid_set_nonblocking(handle, 1);

            return 0;
        }

        public void SetPlayerLED(byte leds_ = 0x0) {
            Subcommand(0x30, new byte[] { leds_ }, 1);
        }

        public void BlinkHomeLight() { // do not call after initial setup
            if (thirdParty)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            a[0] = 0x18;
            a[1] = 0x01;
            Subcommand(0x38, a, 25);
        }

        public void SetHomeLight(bool on) {
            if (thirdParty)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            if (on) {
                a[0] = 0x1F;
                a[1] = 0xF0;
            } else {
                a[0] = 0x10;
                a[1] = 0x01;
            }
            Subcommand(0x38, a, 25);
        }

        private void SetHCIState(byte state) {
            byte[] a = { state };
            Subcommand(0x06, a, 1);
        }

        public void PowerOff() {
            if (state > state_.DROPPED) {
                HIDapi.hid_set_nonblocking(handle, 0);
                SetHCIState(0x00);
                state = state_.DROPPED;
            }
        }

        private void BatteryChanged() { // battery changed level
            foreach (var v in form.con) {
                if (v.Tag == this) {
                    switch (battery) {
                        case 4:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Green);
                            break;
                        case 3:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Green);
                            break;
                        case 2:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.GreenYellow);
                            break;
                        case 1:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Orange);
                            break;
                        default:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Red);
                            break;
                    }
                }
            }

            if (battery <= 1) {
                form.notifyIcon.Visible = true;
                form.notifyIcon.BalloonTipText = String.Format("Controller {0} ({1}) - low battery notification!", PadId, isPro ? "Pro Controller" : (isSnes ? "SNES Controller" : (isLeft ? "Joycon Left" : "Joycon Right")));
                form.notifyIcon.ShowBalloonTip(0);
            }
        }

        public void SetFilterCoeff(float a) {
            filterweight = a;
        }

        public void Detach(bool close = false) {
            stop_polling = true;

            if (out_xbox != null) {
                out_xbox.Disconnect();
            }

            if (out_ds4 != null) {
                out_ds4.Disconnect();
            }

            if (state > state_.NO_JOYCONS) {
                HIDapi.hid_set_nonblocking(handle, 0);

                // Subcommand(0x40, new byte[] { 0x0 }, 1); // disable IMU sensor
                //Subcommand(0x48, new byte[] { 0x0 }, 1); // Would turn off rumble?

                if (isUSB) {
                    byte[] a = Enumerable.Repeat((byte)0, 64).ToArray();
                    a[0] = 0x80; a[1] = 0x5; // Allow device to talk to BT again
                    HIDapi.hid_write(handle, a, new UIntPtr(2));
                    a[0] = 0x80; a[1] = 0x6; // Allow device to talk to BT again
                    HIDapi.hid_write(handle, a, new UIntPtr(2));
                }
            }
            if (close || state > state_.DROPPED) {
                HIDapi.hid_close(handle);
            }
            state = state_.NOT_ATTACHED;
        }

        private byte ts_en;
        private int ReceiveRaw() {
            if (handle == IntPtr.Zero) return -2;
            byte[] raw_buf = new byte[report_len];
            int ret = HIDapi.hid_read_timeout(handle, raw_buf, new UIntPtr(report_len), 5);

            if (ret > 0) {
                // Process packets as soon as they come
                for (int n = 0; n < 3; n++) {
                    ExtractIMUValues(raw_buf, n);

                    byte lag = (byte)Math.Max(0, raw_buf[1] - ts_en - 3);
                    if (n == 0) {
                        Timestamp += (ulong)lag * 5000; // add lag once
                        ProcessButtonsAndStick(raw_buf);

                        // process buttons here to have them affect DS4
                        DoThingsWithButtons();

                        int newbat = battery;
                        battery = (raw_buf[2] >> 4) / 2;
                        if (newbat != battery)
                            BatteryChanged();
                    }
                    Timestamp += 5000; // 5ms difference

                    packetCounter++;
                    if (Program.server != null)
                        Program.server.NewReportIncoming(this);

                    if (out_ds4 != null) {
                        try {
                            out_ds4.UpdateInput(MapToDualShock4Input(this));
                        } catch (Exception e) {
                            // ignore /shrug
                        }
                    }
                }

                // no reason to send XInput reports so often
                if (out_xbox != null) {
                    try {
                        out_xbox.UpdateInput(MapToXbox360Input(this));
                    } catch (Exception e) {
                        // ignore /shrug
                    }
                }


                if (ts_en == raw_buf[1] && !isSnes) {
                    form.AppendTextBox("Duplicate timestamp enqueued.\r\n");
                    DebugPrint(string.Format("Duplicate timestamp enqueued. TS: {0:X2}", ts_en), DebugType.THREADING);
                }
                ts_en = raw_buf[1];
                DebugPrint(string.Format("Enqueue. Bytes read: {0:D}. Timestamp: {1:X2}", ret, raw_buf[1]), DebugType.THREADING);
            }
            return ret;
        }

        public static class ConfigHelper {
            public static MotionMapping GetMotionMapping(string key) {
                // Access the custom section
                var motionMappingsSection = ConfigurationManager.GetSection("motionMappings") as NameValueCollection;
                if (motionMappingsSection == null) {
                    throw new ConfigurationErrorsException("MotionMappings section is missing in the configuration file.");
                }

                var settingValue = motionMappingsSection[key];
                if (string.IsNullOrEmpty(settingValue)) {
                    throw new ConfigurationErrorsException($"MotionMapping configuration for {key} is missing.");
                }

                var parts = settingValue.Split(',');
                if (parts.Length != 8) { // Ensure you have all parts needed for MotionMapping
                    throw new ConfigurationErrorsException($"MotionMapping configuration for {key} is invalid.");
                }

                // Parse and return MotionMapping object
                return new MotionMapping(
                    (State)Enum.Parse(typeof(State), parts[0]),
                    float.Parse(parts[1]),
                    (Button)Enum.Parse(typeof(Button), parts[2]),
                    (Direction)Enum.Parse(typeof(Direction), parts[3]),
                    (Mutate)Enum.Parse(typeof(Mutate), parts[4]),
                    float.Parse(parts[5]),
                    (Button)Enum.Parse(typeof(Button), parts[6]),
                    (Direction)Enum.Parse(typeof(Direction), parts[7])
                );
            }
        }

        private readonly Stopwatch shakeTimer = Stopwatch.StartNew();
        private long shakedTime = 0;
        private bool hasShaked;

        // Define a struct for mapping sensitivity to buttons for each axis and overall shakes
        public struct MotionMapping {
            public State State;
            public float Sensitivity;
            public Button Button;
            public Direction Direction;
            public Mutate MutateState;
            public float MutateSensitivity;
            public Button MutateButton;
            public Direction MutateDirection;

            public MotionMapping(State state, float sensitivity, Button button, Direction direction, Mutate mutateState, float mutateSensitivity, Button mutateButton, Direction mutateDirection) {
                State = state;
                Sensitivity = sensitivity;
                Button = button;
                Direction = direction;
                MutateState = mutateState;
                MutateSensitivity = mutateSensitivity;
                MutateButton = mutateButton;
                MutateDirection = mutateDirection;
            }
        }
        private bool isCurrentlyRumbling = false;

        void TriggerRumbleOnShake() {
            bool enableRumbleOnShake = bool.Parse(ConfigurationManager.AppSettings["EnableRumbleOnShake"]);
            if (enableRumbleOnShake && !isCurrentlyRumbling) {
                int rumbleDuration = int.Parse(ConfigurationManager.AppSettings["RumbleDurationOnShake"]);

                // Read the max frequency and amplitude settings directly
                float maxLowFreq = float.Parse(ConfigurationManager.AppSettings["MaxLowFreqRumbleOnShake"]);
                float maxHighFreq = float.Parse(ConfigurationManager.AppSettings["MaxHighFreqRumbleOnShake"]);
                float maxAmp = float.Parse(ConfigurationManager.AppSettings["MaxAmpRumbleOnShake"]);

                // Start rumbling with max values
                SetRumble(maxLowFreq, maxHighFreq, maxAmp);
                isCurrentlyRumbling = true;

                // Stop rumbling after the duration
                Task.Delay(rumbleDuration).ContinueWith(_ => {
                    SetRumble(0, 0, 0); // Stop rumbling
                    isCurrentlyRumbling = false;
                });
            }
        }

        void ProcessShakeDetection(MotionMapping mapping, float axisValue, long currentShakeTime) {
            // Determine the sensitivity to use (mutator or regular)
            float sensitivityThreshold = mapping.Sensitivity;
            Direction effectiveDirection = mapping.Direction;

            if (mapping.MutateState != Mutate.OFF && IsMutateButtonPressed(mapping.MutateState)) {
                sensitivityThreshold = mapping.MutateSensitivity;
                effectiveDirection = mapping.MutateDirection; // Use mutate direction if mutate condition is met
            }

            bool directionMatch = (effectiveDirection == Direction.POS && axisValue > 0) ||
                                  (effectiveDirection == Direction.NEG && axisValue < 0) ||
                                  (effectiveDirection == Direction.ANY);

            bool isShaking = directionMatch && Math.Pow(axisValue, 2) >= Math.Pow(sensitivityThreshold, 2);


            if ((isShaking) && (currentShakeTime >= shakedTime || shakedTime == 0)) {
                Button buttonToPress = mapping.Button;
                if (mapping.MutateState != Mutate.OFF && IsMutateButtonPressed(mapping.MutateState)) {
                    buttonToPress = mapping.MutateButton;
                }
                shakedTime = currentShakeTime;
                hasShaked = true;
                buttons[(int)buttonToPress] = true;

            } else if ((!isShaking) && hasShaked && currentShakeTime >= shakedTime + 9000) {
                buttons[(int)mapping.Button] = false;
                if (mapping.MutateState != Mutate.OFF) {
                    buttons[(int)mapping.MutateButton] = false;
                }
                hasShaked = false;
            }
            if (hasShaked) {
                // Check if rumble on shake is enabled
                bool enableRumbleOnShake = bool.Parse(ConfigurationManager.AppSettings["EnableRumbleOnShake"]);
                if (hasShaked) {
                    // Trigger rumble with handling to prevent overlap
                    TriggerRumbleOnShake();
                }
            }
        }

        bool IsMutateButtonPressed(Mutate mutateState) {
            // This method checks if a button is currently pressed
            switch (mutateState) {
                case Mutate.SL:
                    return IsButtonPressed(Button.SL);
                case Mutate.SR:
                    return IsButtonPressed(Button.SR);
                case Mutate.DPAD_DOWN:
                    return IsButtonPressed(Button.DPAD_DOWN);
                case Mutate.DPAD_RIGHT:
                    return IsButtonPressed(Button.DPAD_RIGHT);
                case Mutate.DPAD_LEFT:
                    return IsButtonPressed(Button.DPAD_LEFT);
                case Mutate.DPAD_UP:
                    return IsButtonPressed(Button.DPAD_UP);
                case Mutate.A:
                    return IsButtonPressed(Button.A);
                case Mutate.B:
                    return IsButtonPressed(Button.B);
                case Mutate.X:
                    return IsButtonPressed(Button.X);
                case Mutate.Y:
                    return IsButtonPressed(Button.Y);
                case Mutate.MINUS:
                    return IsButtonPressed(Button.MINUS);
                case Mutate.HOME:
                    return IsButtonPressed(Button.HOME);
                case Mutate.PLUS:
                    return IsButtonPressed(Button.PLUS);
                case Mutate.CAPTURE:
                    return IsButtonPressed(Button.CAPTURE);
                case Mutate.STICK:
                    return IsButtonPressed(Button.STICK);
                case Mutate.STICK2:
                    return IsButtonPressed(Button.STICK2);
                case Mutate.SHOULDER_1:
                    return IsButtonPressed(Button.SHOULDER_1);
                case Mutate.SHOULDER2_1:
                    return IsButtonPressed(Button.SHOULDER2_1);
                case Mutate.SHOULDER_2:
                    return IsButtonPressed(Button.SHOULDER_2);
                case Mutate.SHOULDER2_2:
                    return IsButtonPressed(Button.SHOULDER2_2);
                case Mutate.ON:
                    return IsButtonPressed(Button.SL) || IsButtonPressed(Button.SR);
                default:
                    return false;
            }
        }


        void DetectShake() {
            MotionMapping accelShakeSensitivity, accelShakeSensitivityX, accelShakeSensitivityY, accelShakeSensitivityZ;
            MotionMapping gyroShakeSensitivity, gyroShakeSensitivityX, gyroShakeSensitivityY, gyroShakeSensitivityZ, gyroShakeSensitivityY2, gyroShakeSensitivityZ2;

            // Determine which set of motion mappings to use based on PadId and isLeft
            if (this.PadId == 0 || this.PadId == 1) { // Pair 1
                if (this.isLeft) {
                    // Left Joy-Con of Pair 1
                    accelShakeSensitivity = ConfigHelper.GetMotionMapping("Pair1LeftAccelSensitivity");
                    accelShakeSensitivityX = ConfigHelper.GetMotionMapping("Pair1LeftAccelSensitivityX");
                    accelShakeSensitivityY = ConfigHelper.GetMotionMapping("Pair1LeftAccelSensitivityY");
                    accelShakeSensitivityZ = ConfigHelper.GetMotionMapping("Pair1LeftAccelSensitivityZ");

                    gyroShakeSensitivity = ConfigHelper.GetMotionMapping("Pair1LeftGyroSensitivity");
                    gyroShakeSensitivityX = ConfigHelper.GetMotionMapping("Pair1LeftGyroSensitivityX");
                    gyroShakeSensitivityY = ConfigHelper.GetMotionMapping("Pair1LeftGyroSensitivityY");
                    gyroShakeSensitivityZ = ConfigHelper.GetMotionMapping("Pair1LeftGyroSensitivityZ");

                    gyroShakeSensitivityY2 = ConfigHelper.GetMotionMapping("Pair1LeftGyroSensitivityY2");
                    gyroShakeSensitivityZ2 = ConfigHelper.GetMotionMapping("Pair1LeftGyroSensitivityZ2");



                } else {
                    // Right Joy-Con of Pair 1
                    accelShakeSensitivity = ConfigHelper.GetMotionMapping("Pair1RightAccelSensitivity");
                    accelShakeSensitivityX = ConfigHelper.GetMotionMapping("Pair1RightAccelSensitivityX");
                    accelShakeSensitivityY = ConfigHelper.GetMotionMapping("Pair1RightAccelSensitivityY");
                    accelShakeSensitivityZ = ConfigHelper.GetMotionMapping("Pair1RightAccelSensitivityZ");

                    gyroShakeSensitivity = ConfigHelper.GetMotionMapping("Pair1RightGyroSensitivity");
                    gyroShakeSensitivityX = ConfigHelper.GetMotionMapping("Pair1RightGyroSensitivityX");
                    gyroShakeSensitivityY = ConfigHelper.GetMotionMapping("Pair1RightGyroSensitivityY");
                    gyroShakeSensitivityZ = ConfigHelper.GetMotionMapping("Pair1RightGyroSensitivityZ");

                    gyroShakeSensitivityY2 = ConfigHelper.GetMotionMapping("Pair1RightGyroSensitivityY2");
                    gyroShakeSensitivityZ2 = ConfigHelper.GetMotionMapping("Pair1RightGyroSensitivityZ2");

                }
            } else { // Pair 2
                if (this.isLeft) {
                    accelShakeSensitivity = ConfigHelper.GetMotionMapping("Pair2LeftAccelSensitivity");
                    accelShakeSensitivityX = ConfigHelper.GetMotionMapping("Pair2LeftAccelSensitivityX");
                    accelShakeSensitivityY = ConfigHelper.GetMotionMapping("Pair2LeftAccelSensitivityY");
                    accelShakeSensitivityZ = ConfigHelper.GetMotionMapping("Pair2LeftAccelSensitivityZ");

                    gyroShakeSensitivity = ConfigHelper.GetMotionMapping("Pair2LeftGyroSensitivity");
                    gyroShakeSensitivityX = ConfigHelper.GetMotionMapping("Pair2LeftGyroSensitivityX");
                    gyroShakeSensitivityY = ConfigHelper.GetMotionMapping("Pair2LeftGyroSensitivityY");
                    gyroShakeSensitivityZ = ConfigHelper.GetMotionMapping("Pair2LeftGyroSensitivityZ");

                    gyroShakeSensitivityY2 = ConfigHelper.GetMotionMapping("Pair2LeftGyroSensitivityY2");
                    gyroShakeSensitivityZ2 = ConfigHelper.GetMotionMapping("Pair2LeftGyroSensitivityZ2");
                } else {
                    // Right Joy-Con of Pair 2
                    accelShakeSensitivity = ConfigHelper.GetMotionMapping("Pair2RightAccelSensitivity");
                    accelShakeSensitivityX = ConfigHelper.GetMotionMapping("Pair2RightAccelSensitivityX");
                    accelShakeSensitivityY = ConfigHelper.GetMotionMapping("Pair2RightAccelSensitivityY");
                    accelShakeSensitivityZ = ConfigHelper.GetMotionMapping("Pair2RightAccelSensitivityZ");

                    gyroShakeSensitivity = ConfigHelper.GetMotionMapping("Pair2RightGyroSensitivity");
                    gyroShakeSensitivityX = ConfigHelper.GetMotionMapping("Pair2RightGyroSensitivityX");
                    gyroShakeSensitivityY = ConfigHelper.GetMotionMapping("Pair2RightGyroSensitivityY");
                    gyroShakeSensitivityZ = ConfigHelper.GetMotionMapping("Pair2RightGyroSensitivityZ");

                    gyroShakeSensitivityY2 = ConfigHelper.GetMotionMapping("Pair2RightGyroSensitivityY2");
                    gyroShakeSensitivityZ2 = ConfigHelper.GetMotionMapping("Pair2RightGyroSensitivityZ2");
                }
            }

            long currentShakeTime = shakeTimer.ElapsedMilliseconds;
            // Process shake detection for each mapping
            ProcessShakeDetectionIfEnabled(accelShakeSensitivity, GetAccel().LengthSquared(), currentShakeTime);
            ProcessShakeDetectionIfEnabled(accelShakeSensitivityX, GetAccel().X, currentShakeTime);
            ProcessShakeDetectionIfEnabled(accelShakeSensitivityY, GetAccel().Y, currentShakeTime);
            ProcessShakeDetectionIfEnabled(accelShakeSensitivityZ, GetAccel().Z, currentShakeTime);

            Vector3 gyroData = GetGyro();
            float gyroYandZLengthSquared = (float)(Math.Pow(gyroData.Y, 2) + Math.Pow(gyroData.Z, 2)) / 1000;
            ProcessShakeDetectionIfEnabled(gyroShakeSensitivity, gyroYandZLengthSquared, currentShakeTime);
            ProcessShakeDetectionIfEnabled(gyroShakeSensitivityX, GetGyro().X, currentShakeTime);
            ProcessShakeDetectionIfEnabled(gyroShakeSensitivityY, GetGyro().Y, currentShakeTime);
            ProcessShakeDetectionIfEnabled(gyroShakeSensitivityZ, GetGyro().Z, currentShakeTime);
            ProcessShakeDetectionIfEnabled(gyroShakeSensitivityY2, GetGyro().Y, currentShakeTime);
            ProcessShakeDetectionIfEnabled(gyroShakeSensitivityZ2, GetGyro().Z, currentShakeTime);





        }


        void ResetButtonStates() {
            // Implement logic to reset all buttons to their "not pressed" state
            // This could involve iterating over a collection of all mapped buttons and setting their states to false
        }

        void ProcessShakeDetectionIfEnabled(MotionMapping mapping, float axisValue, long currentShakeTime) {
            // Check if shake detection should proceed based on the regular state or the mutator state
            bool proceedWithDetection = mapping.State == State.ON ||
                                        (mapping.MutateState != Mutate.OFF && IsMutateButtonPressed(mapping.MutateState));

            if (proceedWithDetection) {
                ProcessShakeDetection(mapping, axisValue, currentShakeTime);
            }
        }


        bool dragToggle = Boolean.Parse(ConfigurationManager.AppSettings["DragToggle"]);
        Dictionary<int, bool> mouse_toggle_btn = new Dictionary<int, bool>();
        private void Simulate(string s, bool click = true, bool up = false) {
            if (s.StartsWith("key_")) {
                WindowsInput.Events.KeyCode key = (WindowsInput.Events.KeyCode)Int32.Parse(s.Substring(4));
                if (click) {
                    WindowsInput.Simulate.Events().Click(key).Invoke();
                } else {
                    if (up) {
                        WindowsInput.Simulate.Events().Release(key).Invoke();
                    } else {
                        WindowsInput.Simulate.Events().Hold(key).Invoke();
                    }
                }
            } else if (s.StartsWith("mse_")) {
                WindowsInput.Events.ButtonCode button = (WindowsInput.Events.ButtonCode)Int32.Parse(s.Substring(4));
                if (click) {
                    WindowsInput.Simulate.Events().Click(button).Invoke();
                } else {
                    if (dragToggle) {
                        if (!up) {
                            bool release;
                            mouse_toggle_btn.TryGetValue((int)button, out release);
                            if (release)
                                WindowsInput.Simulate.Events().Release(button).Invoke();
                            else
                                WindowsInput.Simulate.Events().Hold(button).Invoke();
                            mouse_toggle_btn[(int)button] = !release;
                        }
                    } else {
                        if (up) {
                            WindowsInput.Simulate.Events().Release(button).Invoke();
                        } else {
                            WindowsInput.Simulate.Events().Hold(button).Invoke();
                        }
                    }
                }
            }
        }

        // For Joystick->Joystick inputs
        private void SimulateContinous(int origin, string s) {
            if (s.StartsWith("joy_")) {
                int button = Int32.Parse(s.Substring(4));
                buttons[button] |= buttons[origin];
            }
        }

        bool HomeLongPowerOff = Boolean.Parse(ConfigurationManager.AppSettings["HomeLongPowerOff"]);
        long PowerOffInactivityMins = Int32.Parse(ConfigurationManager.AppSettings["PowerOffInactivity"]);

        bool ChangeOrientationDoubleClick = Boolean.Parse(ConfigurationManager.AppSettings["ChangeOrientationDoubleClick"]);
        long lastDoubleClick = -1;

        string extraGyroFeature = ConfigurationManager.AppSettings["GyroToJoyOrMouse"];
        bool UseFilteredIMU = Boolean.Parse(ConfigurationManager.AppSettings["UseFilteredIMU"]);
        int GyroMouseSensitivityX = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityX"]);
        int GyroMouseSensitivityY = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityY"]);
        float GyroStickSensitivityX = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityX"]);
        float GyroStickSensitivityY = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityY"]);
        float GyroStickReduction = float.Parse(ConfigurationManager.AppSettings["GyroStickReduction"]);
        bool GyroHoldToggle = Boolean.Parse(ConfigurationManager.AppSettings["GyroHoldToggle"]);
        bool GyroAnalogSliders = Boolean.Parse(ConfigurationManager.AppSettings["GyroAnalogSliders"]);
        int GyroAnalogSensitivity = Int32.Parse(ConfigurationManager.AppSettings["GyroAnalogSensitivity"]);
        byte[] sliderVal = new byte[] { 0, 0 };

        private void DoThingsWithButtons() {
            int powerOffButton = (int)((isPro || !isLeft || other != null) ? Button.HOME : Button.CAPTURE);

            long timestamp = Stopwatch.GetTimestamp();
            if (HomeLongPowerOff && buttons[powerOffButton]) {
                if ((timestamp - buttons_down_timestamp[powerOffButton]) / 10000 > 2000.0) {
                    if (other != null)
                        other.PowerOff();

                    PowerOff();
                    return;
                }
            }

            if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && lastDoubleClick != -1 && !isPro) {
                if ((buttons_down_timestamp[(int)Button.STICK] - lastDoubleClick) < 3000000) {
                    form.conBtnClick(form.con[PadId], EventArgs.Empty); // trigger connection button click

                    lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
                    return;
                }
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            } else if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && !isPro) {
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            }

            if (PowerOffInactivityMins > 0) {
                if ((timestamp - inactivity) / 10000 > PowerOffInactivityMins * 60 * 1000) {
                    if (other != null)
                        other.PowerOff();

                    PowerOff();
                    return;
                }
            }

            DetectShake();

            if (buttons_down[(int)Button.CAPTURE])
                Simulate(Config.Value("capture"));
            if (buttons_down[(int)Button.HOME])
                Simulate(Config.Value("home"));
            SimulateContinous((int)Button.CAPTURE, Config.Value("capture"));
            SimulateContinous((int)Button.HOME, Config.Value("home"));

            if (isLeft) {
                if (buttons_down[(int)Button.SL])
                    Simulate(Config.Value("sl_l"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(Config.Value("sl_l"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(Config.Value("sr_l"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(Config.Value("sr_l"), false, true);

                SimulateContinous((int)Button.SL, Config.Value("sl_l"));
                SimulateContinous((int)Button.SR, Config.Value("sr_l"));
            } else {
                if (buttons_down[(int)Button.SL])
                    Simulate(Config.Value("sl_r"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(Config.Value("sl_r"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(Config.Value("sr_r"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(Config.Value("sr_r"), false, true);

                SimulateContinous((int)Button.SL, Config.Value("sl_r"));
                SimulateContinous((int)Button.SR, Config.Value("sr_r"));
            }

            // Filtered IMU data
            this.cur_rotation = AHRS.GetEulerAngles();
            float dt = 0.015f; // 15ms

            if (GyroAnalogSliders && (other != null || isPro)) {
                Button leftT = isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2;
                Button rightT = isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2;
                Joycon left = isLeft ? this : (isPro ? this : this.other); Joycon right = !isLeft ? this : (isPro ? this : this.other);

                int ldy, rdy;
                if (UseFilteredIMU) {
                    ldy = (int)(GyroAnalogSensitivity * (left.cur_rotation[0] - left.cur_rotation[3]));
                    rdy = (int)(GyroAnalogSensitivity * (right.cur_rotation[0] - right.cur_rotation[3]));
                } else {
                    ldy = (int)(GyroAnalogSensitivity * (left.gyr_g.Y * dt));
                    rdy = (int)(GyroAnalogSensitivity * (right.gyr_g.Y * dt));
                }

                if (buttons[(int)leftT]) {
                    sliderVal[0] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[0] + ldy));
                } else {
                    sliderVal[0] = 0;
                }

                if (buttons[(int)rightT]) {
                    sliderVal[1] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[1] + rdy));
                } else {
                    sliderVal[1] = 0;
                }
            }

            string res_val = Config.Value("active_gyro");
            if (res_val.StartsWith("joy_")) {
                int i = Int32.Parse(res_val.Substring(4));
                if (GyroHoldToggle) {
                    if (buttons_down[i]) // Removed the check for other.buttons_down[i]
                        active_gyro = true;
                    else if (buttons_up[i]) // Removed the check for other.buttons_up[i]
                        active_gyro = false;
                } else {
                    if (buttons_down[i]) // Removed the check for other.buttons_down[i]
                        active_gyro = !active_gyro;
                }
            }

            if (!isLeft) {
                if (extraGyroFeature.Substring(0, 3) == "joy") {
                    if (Config.Value("active_gyro") == "0" || active_gyro) {
                        float[] control_stick = stick2; // Directly use stick2, intended for right Joy-Con

                        float dx, dy;
                        if (UseFilteredIMU) {
                            dx = (GyroStickSensitivityX * (cur_rotation[1] - cur_rotation[4])); // yaw
                            dy = (GyroStickSensitivityY * (cur_rotation[0] - cur_rotation[3])); // Adjusted pitch direction
                        } else {
                            dx = (GyroStickSensitivityX * (gyr_g.Z * dt)); // yaw
                            dy = (GyroStickSensitivityY * (gyr_g.Y * dt)); // Adjusted pitch direction
                        }

                        control_stick[0] = Math.Max(-1.0f, Math.Min(1.0f, control_stick[0] / GyroStickReduction + dx));
                        control_stick[1] = Math.Max(-1.0f, Math.Min(1.0f, control_stick[1] / GyroStickReduction + dy));
                    }
                }
            } else if (extraGyroFeature == "mouse" && (isPro || (other == null) || (other != null && (Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseLeftHanded"]) ? isLeft : !isLeft)))) {
                // gyro data is in degrees/s
                if (Config.Value("active_gyro") == "0" || active_gyro) {
                    int dx, dy;

                    if (UseFilteredIMU) {
                        dx = (int)(GyroMouseSensitivityX * (cur_rotation[1] - cur_rotation[4])); // yaw
                        dy = (int)-(GyroMouseSensitivityY * (cur_rotation[0] - cur_rotation[3])); // pitch
                    } else {
                        dx = (int)(GyroMouseSensitivityX * (gyr_g.Z * dt));
                        dy = (int)-(GyroMouseSensitivityY * (gyr_g.Y * dt));
                    }

                    WindowsInput.Simulate.Events().MoveBy(dx, dy).Invoke();
                }

                // reset mouse position to centre of primary monitor
                res_val = Config.Value("reset_mouse");
                if (res_val.StartsWith("joy_")) {
                    int i = Int32.Parse(res_val.Substring(4));
                    if (buttons_down[i] || (other != null && other.buttons_down[i]))
                        WindowsInput.Simulate.Events().MoveTo(Screen.PrimaryScreen.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Height / 2).Invoke();
                }
            }
        }

        private Thread PollThreadObj;
        private void Poll() {
            stop_polling = false;
            int attempts = 0;
            while (!stop_polling & state > state_.NO_JOYCONS) {
                if (rumble_obj.queue.Count > 0) {
                    SendRumble(rumble_obj.GetData());
                }
                int a = ReceiveRaw();

                if (a > 0 && state > state_.DROPPED) {
                    state = state_.IMU_DATA_OK;
                    attempts = 0;
                } else if (attempts > 240) {
                    state = state_.DROPPED;
                    form.AppendTextBox("Dropped.\r\n");

                    DebugPrint("Connection lost. Is the Joy-Con connected?", DebugType.ALL);
                    break;
                } else if (a < 0) {
                    // An error on read.
                    //form.AppendTextBox("Pause 5ms");
                    Thread.Sleep((Int32)5);
                    ++attempts;
                } else if (a == 0) {
                    // The non-blocking read timed out. No need to sleep.
                    // No need to increase attempts because it's not an error.
                }
            }
        }

        public float[] otherStick = { 0, 0 };

        bool swapAB = Boolean.Parse(ConfigurationManager.AppSettings["SwapAB"]);
        bool swapXY = Boolean.Parse(ConfigurationManager.AppSettings["SwapXY"]);
        private int ProcessButtonsAndStick(byte[] report_buf) {
            if (report_buf[0] == 0x00) throw new ArgumentException("received undefined report. This is probably a bug");
            if (!isSnes) {
                stick_raw[0] = report_buf[6 + (isLeft ? 0 : 3)];
                stick_raw[1] = report_buf[7 + (isLeft ? 0 : 3)];
                stick_raw[2] = report_buf[8 + (isLeft ? 0 : 3)];

                if (isPro) {
                    stick2_raw[0] = report_buf[6 + (!isLeft ? 0 : 3)];
                    stick2_raw[1] = report_buf[7 + (!isLeft ? 0 : 3)];
                    stick2_raw[2] = report_buf[8 + (!isLeft ? 0 : 3)];
                }

                stick_precal[0] = (UInt16)(stick_raw[0] | ((stick_raw[1] & 0xf) << 8));
                stick_precal[1] = (UInt16)((stick_raw[1] >> 4) | (stick_raw[2] << 4));
                stick = CenterSticks(stick_precal, stick_cal, deadzone);

                if (isPro) {
                    stick2_precal[0] = (UInt16)(stick2_raw[0] | ((stick2_raw[1] & 0xf) << 8));
                    stick2_precal[1] = (UInt16)((stick2_raw[1] >> 4) | (stick2_raw[2] << 4));
                    stick2 = CenterSticks(stick2_precal, stick2_cal, deadzone2);
                }

                // Read other Joycon's sticks
                if (isLeft && other != null && other != this) {
                    stick2 = otherStick;
                    other.otherStick = stick;
                }

                if (!isLeft && other != null && other != this) {
                    Array.Copy(stick, stick2, 2);
                    stick = otherStick;
                    other.otherStick = stick2;
                }
            }
            //

            // Set button states both for server and ViGEm
            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; ++i) {
                        down_[i] = buttons[i];
                    }
                }
                buttons = new bool[20];

                buttons[(int)Button.DPAD_DOWN] = (report_buf[5] & 0x01) != 0;
                buttons[(int)Button.DPAD_RIGHT] = (report_buf[5] & 0x04) != 0;
                buttons[(int)Button.DPAD_UP] = (report_buf[5] & 0x02) != 0;
                buttons[(int)Button.DPAD_LEFT] = (report_buf[5] & 0x08) != 0;
                buttons[(int)Button.HOME] = ((report_buf[4] & 0x10) != 0);
                buttons[(int)Button.CAPTURE] = ((report_buf[4] & 0x20) != 0);
                buttons[(int)Button.MINUS] = ((report_buf[4] & 0x01) != 0);
                buttons[(int)Button.PLUS] = ((report_buf[4] & 0x02) != 0);
                buttons[(int)Button.STICK] = ((report_buf[4] & (isLeft ? 0x08 : 0x04)) != 0);
                buttons[(int)Button.SHOULDER_1] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x40) != 0; // L button
                buttons[(int)Button.SHOULDER_2] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x80) != 0; // RL button
                buttons[(int)Button.SR] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x10) != 0;
                buttons[(int)Button.SL] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x20) != 0;

                if (isPro) {
                    buttons[(int)Button.B] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x01 : 0x04)) != 0;
                    buttons[(int)Button.A] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x04 : 0x08)) != 0;
                    buttons[(int)Button.X] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x02 : 0x02)) != 0;
                    buttons[(int)Button.Y] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x08 : 0x01)) != 0;

                    buttons[(int)Button.STICK2] = ((report_buf[4] & (!isLeft ? 0x08 : 0x04)) != 0);
                    buttons[(int)Button.SHOULDER2_1] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x40) != 0;
                    buttons[(int)Button.SHOULDER2_2] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x80) != 0;
                }

                if (other != null && other != this) {
                    buttons[(int)(Button.B)] = (report_buf[3] & 0x04) != 0;
                    buttons[(int)(Button.A)] = (report_buf[3] & 0x08) != 0;
                    buttons[(int)(Button.X)] = (report_buf[3] & 0x02) != 0;
                    buttons[(int)(Button.Y)] = (report_buf[3] & 0x01) != 0;

                    buttons[(int)Button.STICK2] = other.buttons[(int)Button.STICK];
                    buttons[(int)Button.SHOULDER2_1] = other.buttons[(int)Button.SHOULDER_1]; // R button
                    buttons[(int)Button.SHOULDER2_2] = other.buttons[(int)Button.SHOULDER_2]; // ZR button
                }

                if (isLeft && other != null && other != this) {
                    buttons[(int)Button.HOME] = other.buttons[(int)Button.HOME];
                    buttons[(int)Button.PLUS] = other.buttons[(int)Button.PLUS];


                }

                if (!isLeft && other != null && other != this) {
                    buttons[(int)Button.MINUS] = other.buttons[(int)Button.MINUS];
                }


                long timestamp = Stopwatch.GetTimestamp();

                lock (buttons_up) {
                    lock (buttons_down) {
                        bool changed = false;
                        for (int i = 0; i < buttons.Length; ++i) {
                            buttons_up[i] = (down_[i] & !buttons[i]);
                            buttons_down[i] = (!down_[i] & buttons[i]);
                            if (down_[i] != buttons[i])
                                buttons_down_timestamp[i] = (buttons[i] ? timestamp : -1);
                            if (buttons_up[i] || buttons_down[i])
                                changed = true;
                        }

                        inactivity = (changed) ? timestamp : inactivity;
                    }
                }
            }

            return 0;
        }

        // Get Gyro/Accel data
        private void ExtractIMUValues(byte[] report_buf, int n = 0) {
            if (!isSnes) {
                gyr_r[0] = (Int16)(report_buf[19 + n * 12] | ((report_buf[20 + n * 12] << 8) & 0xff00));
                gyr_r[1] = (Int16)(report_buf[21 + n * 12] | ((report_buf[22 + n * 12] << 8) & 0xff00));
                gyr_r[2] = (Int16)(report_buf[23 + n * 12] | ((report_buf[24 + n * 12] << 8) & 0xff00));
                acc_r[0] = (Int16)(report_buf[13 + n * 12] | ((report_buf[14 + n * 12] << 8) & 0xff00));
                acc_r[1] = (Int16)(report_buf[15 + n * 12] | ((report_buf[16 + n * 12] << 8) & 0xff00));
                acc_r[2] = (Int16)(report_buf[17 + n * 12] | ((report_buf[18 + n * 12] << 8) & 0xff00));

                if (form.allowCalibration) {
                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - activeData[3]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.X = (gyr_r[i] - activeData[0]) * (816.0f / gyr_sen[i]);
                                if (form.calibrate) {
                                    form.xA.Add(acc_r[i]);
                                    form.xG.Add(gyr_r[i]);
                                }
                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[4]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[1]) * (816.0f / gyr_sen[i]);
                                if (form.calibrate) {
                                    form.yA.Add(acc_r[i]);
                                    form.yG.Add(gyr_r[i]);
                                }
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[5]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[2]) * (816.0f / gyr_sen[i]);
                                if (form.calibrate) {
                                    form.zA.Add(acc_r[i]);
                                    form.zG.Add(gyr_r[i]);
                                }
                                break;
                        }
                    }
                } else {
                    Int16[] offset;
                    if (isPro)
                        offset = pro_hor_offset;
                    else if (isLeft)
                        offset = left_hor_offset;
                    else
                        offset = right_hor_offset;

                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.X = (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));

                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                        }
                    }
                }

                if (other == null && !isPro) { // single joycon mode; Z do not swap, rest do
                    if (isLeft) {
                        acc_g.X = -acc_g.X;
                        acc_g.Y = -acc_g.Y;
                        gyr_g.X = -gyr_g.X;
                    } else {
                        gyr_g.Y = -gyr_g.Y;
                    }

                    float temp = acc_g.X;
                    acc_g.X = acc_g.Y;
                    acc_g.Y = -temp;

                    temp = gyr_g.X;
                    gyr_g.X = gyr_g.Y;
                    gyr_g.Y = temp;
                }

                // Update rotation Quaternion
                float deg_to_rad = 0.0174533f;
                AHRS.Update(gyr_g.X * deg_to_rad, gyr_g.Y * deg_to_rad, gyr_g.Z * deg_to_rad, acc_g.X, acc_g.Y, acc_g.Z);
            }
        }

        public void Begin() {
            if (PollThreadObj == null) {
                PollThreadObj = new Thread(new ThreadStart(Poll));
                PollThreadObj.IsBackground = true;
                PollThreadObj.Start();

                form.AppendTextBox("Starting poll thread.\r\n");
            } else {
                form.AppendTextBox("Poll cannot start.\r\n");
            }
        }

        // Should really be called calculating stick data
        private float[] CenterSticks(UInt16[] vals, ushort[] cal, ushort dz) {
            ushort[] t = cal;

            float[] s = { 0, 0 };
            float dx = vals[0] - t[2], dy = vals[1] - t[3];
            if (Math.Abs(dx * dx + dy * dy) < dz * dz)
                return s;

            s[0] = dx / (dx > 0 ? t[0] : t[4]);
            s[1] = dy / (dy > 0 ? t[1] : t[5]);
            return s;
        }

        private static short CastStickValue(float stick_value) {
            return (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, stick_value * (stick_value > 0 ? Int16.MaxValue : -Int16.MinValue)));
        }

        private static byte CastStickValueByte(float stick_value) {
            return (byte)Math.Max(Byte.MinValue, Math.Min(Byte.MaxValue, 127 - stick_value * Byte.MaxValue));
        }

        public void SetRumble(float low_freq, float high_freq, float amp) {
            if (state <= Joycon.state_.ATTACHED) return;
            rumble_obj.set_vals(low_freq, high_freq, amp);
        }

        private void SendRumble(byte[] buf) {
            byte[] buf_ = new byte[report_len];
            buf_[0] = 0x10;
            buf_[1] = global_count;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            Array.Copy(buf, 0, buf_, 2, 8);
            PrintArray(buf_, DebugType.RUMBLE, format: "Rumble data sent: {0:S}");
            HIDapi.hid_write(handle, buf_, new UIntPtr(report_len));
        }

        private byte[] Subcommand(byte sc, byte[] buf, uint len, bool print = true) {
            byte[] buf_ = new byte[report_len];
            byte[] response = new byte[report_len];
            Array.Copy(default_buf, 0, buf_, 2, 8);
            Array.Copy(buf, 0, buf_, 11, len);
            buf_[10] = sc;
            buf_[1] = global_count;
            buf_[0] = 0x1;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            if (print) { PrintArray(buf_, DebugType.COMMS, len, 11, "Subcommand 0x" + string.Format("{0:X2}", sc) + " sent. Data: 0x{0:S}"); };
            HIDapi.hid_write(handle, buf_, new UIntPtr(len + 11));
            int tries = 0;
            do {
                int res = HIDapi.hid_read_timeout(handle, response, new UIntPtr(report_len), 100);
                if (res < 1) DebugPrint("No response.", DebugType.COMMS);
                else if (print) { PrintArray(response, DebugType.COMMS, report_len - 1, 1, "Response ID 0x" + string.Format("{0:X2}", response[0]) + ". Data: 0x{0:S}"); }
                tries++;
            } while (tries < 10 && response[0] != 0x21 && response[14] != sc);

            return response;
        }

        private void dump_calibration_data() {
            if (isSnes || thirdParty) {
                short[] temp = (short[])ConfigurationManager.AppSettings["acc_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                acc_sensiti[0] = temp[0]; acc_sensiti[1] = temp[1]; acc_sensiti[2] = temp[2];
                temp = (short[])ConfigurationManager.AppSettings["gyr_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                gyr_sensiti[0] = temp[0]; gyr_sensiti[1] = temp[1]; gyr_sensiti[2] = temp[2];
                ushort[] temp2 = (ushort[])ConfigurationManager.AppSettings["stick_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick_cal[0] = temp2[0]; stick_cal[1] = temp2[1]; stick_cal[2] = temp2[2];
                stick_cal[3] = temp2[3]; stick_cal[4] = temp2[4]; stick_cal[5] = temp2[5];
                deadzone = ushort.Parse(ConfigurationManager.AppSettings["deadzone"]);
                temp2 = (ushort[])ConfigurationManager.AppSettings["stick2_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick2_cal[0] = temp2[0]; stick2_cal[1] = temp2[1]; stick2_cal[2] = temp2[2];
                stick2_cal[3] = temp2[3]; stick2_cal[4] = temp2[4]; stick2_cal[5] = temp2[5];
                deadzone2 = ushort.Parse(ConfigurationManager.AppSettings["deadzone2"]);
                return;
            }

            HIDapi.hid_set_nonblocking(handle, 0);
            byte[] buf_ = ReadSPI(0x80, (isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
            bool found = false;
            for (int i = 0; i < 9; ++i) {
                if (buf_[i] != 0xff) {
                    form.AppendTextBox("Using user stick calibration data.\r\n");
                    found = true;
                    break;
                }
            }
            if (!found) {
                form.AppendTextBox("Using factory stick calibration data.\r\n");
                buf_ = ReadSPI(0x60, (isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
            }
            stick_cal[isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
            stick_cal[isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
            stick_cal[isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
            stick_cal[isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
            stick_cal[isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
            stick_cal[isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

            PrintArray(stick_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

            if (isPro) {
                buf_ = ReadSPI(0x80, (!isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
                found = false;
                for (int i = 0; i < 9; ++i) {
                    if (buf_[i] != 0xff) {
                        form.AppendTextBox("Using user stick calibration data.\r\n");
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    form.AppendTextBox("Using factory stick calibration data.\r\n");
                    buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
                }
                stick2_cal[!isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
                stick2_cal[!isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
                stick2_cal[!isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
                stick2_cal[!isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
                stick2_cal[!isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
                stick2_cal[!isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

                PrintArray(stick2_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

                buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x86 : (byte)0x98), 16);
                deadzone2 = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);
            }

            buf_ = ReadSPI(0x60, (isLeft ? (byte)0x86 : (byte)0x98), 16);
            deadzone = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);

            buf_ = ReadSPI(0x80, 0x28, 10);
            acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x2E, 10);
            acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x34, 10);
            gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x3A, 10);
            gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "User gyro neutral position: {0:S}");

            // This is an extremely messy way of checking to see whether there is user stick calibration data present, but I've seen conflicting user calibration data on blank Joy-Cons. Worth another look eventually.
            if (gyr_neutral[0] + gyr_neutral[1] + gyr_neutral[2] == -3 || Math.Abs(gyr_neutral[0]) > 100 || Math.Abs(gyr_neutral[1]) > 100 || Math.Abs(gyr_neutral[2]) > 100) {
                buf_ = ReadSPI(0x60, 0x20, 10);
                acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x26, 10);
                acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x2C, 10);
                gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x32, 10);
                gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "Factory gyro neutral position: {0:S}");
            }
            HIDapi.hid_set_nonblocking(handle, 1);
        }

        private byte[] ReadSPI(byte addr1, byte addr2, uint len, bool print = false) {
            byte[] buf = { addr2, addr1, 0x00, 0x00, (byte)len };
            byte[] read_buf = new byte[len];
            byte[] buf_ = new byte[len + 20];

            for (int i = 0; i < 100; ++i) {
                buf_ = Subcommand(0x10, buf, 5, false);
                if (buf_[15] == addr2 && buf_[16] == addr1) {
                    break;
                }
            }
            Array.Copy(buf_, 20, read_buf, 0, len);
            if (print) PrintArray(read_buf, DebugType.COMMS, len);
            return read_buf;
        }

        private void PrintArray<T>(T[] arr, DebugType d = DebugType.NONE, uint len = 0, uint start = 0, string format = "{0:S}") {
            if (d != debug_type && debug_type != DebugType.ALL) return;
            if (len == 0) len = (uint)arr.Length;
            string tostr = "";
            for (int i = 0; i < len; ++i) {
                tostr += string.Format((arr[0] is byte) ? "{0:X2} " : ((arr[0] is float) ? "{0:F} " : "{0:D} "), arr[i + start]);
            }
            DebugPrint(string.Format(format, tostr), d);
        }



        private static OutputControllerXbox360InputState MapToXbox360Input(Joycon input) {
            var output = new OutputControllerXbox360InputState();

            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;



            if (isPro) {

            } else {
                if (other != null) { // no need for && other != this
                    ButtonFunctionality GetButtonFunctionality(string key) {
                        string value = ConfigurationManager.AppSettings[key];
                        // Parse the value to your ButtonFunctionality enum. Assuming you have such an enum.
                        if (Enum.TryParse(value, out ButtonFunctionality result)) {
                            return result;
                        }
                        return ButtonFunctionality.OFF; // Default or error handling
                    }
                    // MAP SL AND SR BUTTONS TO OTHER BUTTONS HERE! (For instance if you map the A button to SRO, pressing the SR button on the right joy-con will activate the A button)
                    // Change the last part of the code to .OFF .SL .SR .SLO or .SRO. and make sure to close it with the ; symbol
                    // The left and right trigger cannot be mapped from here and requires you to scroll down a bit more.

                    ButtonFunctionality aFunctionality = GetButtonFunctionality("SpecialHotkeyA");
                    ButtonFunctionality bFunctionality = GetButtonFunctionality("SpecialHotkeyB");
                    ButtonFunctionality yFunctionality = GetButtonFunctionality("SpecialHotkeyY");
                    ButtonFunctionality xFunctionality = GetButtonFunctionality("SpecialHotkeyX");

                    ButtonFunctionality dpadUpFunctionality = GetButtonFunctionality("SpecialHotkeyUP"); // DPAD UP, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable
                    ButtonFunctionality dpadDownFunctionality = GetButtonFunctionality("SpecialHotkeyDOWN"); // DPAD DOWN, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable
                    ButtonFunctionality dpadLeftFunctionality = GetButtonFunctionality("SpecialHotkeyLEFT"); // DPAD LEFT, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable
                    ButtonFunctionality dpadRightFunctionality = GetButtonFunctionality("SpecialHotkeyRIGHT"); // DPAD RIGHT, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable

                    ButtonFunctionality backFunctionality = GetButtonFunctionality("SpecialHotkeyBACK"); // MINUS BUTTON, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable
                    ButtonFunctionality startFunctionality = GetButtonFunctionality("SpecialHotkeySTART"); // PLUS BUTTON, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable
                    ButtonFunctionality guideFunctionality = GetButtonFunctionality("SpecialHotkeyGUIDE"); // GUIDE??? BUTTON, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable

                    ButtonFunctionality shoulderLeftFunctionality = GetButtonFunctionality("SpecialHotkeyLB"); // L BUTTON, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable
                    ButtonFunctionality shoulderRightFunctionality = GetButtonFunctionality("SpecialHotkeyRB"); // R BUTTON, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable

                    ButtonFunctionality thumbStickLeftFunctionality = GetButtonFunctionality("SpecialHotkeyLS"); ; // LEFT STICK BUTTON, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable
                    ButtonFunctionality thumbStickRightFunctionality = GetButtonFunctionality("SpecialHotkeyRS"); // RIGHT STICK BUTTON, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF; to disable


                    output.a = buttons[(int)(!swapAB ? Button.B : Button.A)] || other.buttons[(int)Button.B] ||
                               (aFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                                aFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                                aFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] :
                                aFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] : false);

                    output.b = buttons[(int)(!swapAB ? Button.A : Button.B)] || other.buttons[(int)Button.A] ||
                               (bFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                                bFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                                bFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] :
                                bFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] : false);

                    output.y = buttons[(int)(!swapXY ? Button.X : Button.Y)] || other.buttons[(int)Button.X] ||
                               (yFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                                yFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                                yFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] :
                                yFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] : false);

                    output.x = buttons[(int)(!swapXY ? Button.Y : Button.X)] || other.buttons[(int)Button.Y] ||
                               (xFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                                xFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                                xFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] :
                                xFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] : false);

                    output.dpad_up = buttons[(int)Button.DPAD_UP] || other.buttons[(int)Button.DPAD_UP] ||
                        (dpadUpFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         dpadUpFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         dpadUpFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         dpadUpFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);
                    output.dpad_down = buttons[(int)Button.DPAD_DOWN] || other.buttons[(int)Button.DPAD_DOWN] ||
                        (dpadDownFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         dpadDownFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         dpadDownFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         dpadDownFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);
                    output.dpad_left = buttons[(int)Button.DPAD_LEFT] || other.buttons[(int)Button.DPAD_LEFT] ||
                        (dpadLeftFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         dpadLeftFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         dpadLeftFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         dpadLeftFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);
                    output.dpad_right = buttons[(int)Button.DPAD_RIGHT] || other.buttons[(int)Button.DPAD_RIGHT] ||
                        (dpadRightFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         dpadRightFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         dpadRightFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         dpadRightFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);

                    output.back = buttons[(int)Button.MINUS] || other.buttons[(int)Button.MINUS] ||
                        (backFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         backFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         backFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         backFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);
                    output.start = buttons[(int)Button.PLUS] ||
                        (startFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         startFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         startFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         startFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);
                    output.guide = buttons[(int)Button.HOME] ||
                        (guideFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         guideFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         guideFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         guideFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);

                    output.shoulder_left = buttons[(int)Button.SHOULDER_1] || other.buttons[(int)Button.SHOULDER2_1] ||
                        (shoulderLeftFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         shoulderLeftFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         shoulderLeftFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         shoulderLeftFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);     //L button
                    output.shoulder_right = buttons[(int)Button.SHOULDER2_1] ||
                        (shoulderRightFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         shoulderRightFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         shoulderRightFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         shoulderRightFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);  //R button

                    output.thumb_stick_left = buttons[(int)Button.STICK] || other.buttons[(int)Button.STICK2] ||
                        (thumbStickLeftFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         thumbStickLeftFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         thumbStickLeftFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         thumbStickLeftFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);
                    output.thumb_stick_right = buttons[(int)Button.STICK2] ||
                        (thumbStickRightFunctionality == ButtonFunctionality.SL ? buttons[(int)Button.SL] :
                         thumbStickRightFunctionality == ButtonFunctionality.SR ? buttons[(int)Button.SR] :
                         thumbStickRightFunctionality == ButtonFunctionality.SLO ? other.buttons[(int)Button.SL] :
                         thumbStickRightFunctionality == ButtonFunctionality.SRO ? other.buttons[(int)Button.SR] : false);
                } else { // single joycon mode
                    output.a = buttons[(int)(!swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.b = buttons[(int)(swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.y = buttons[(int)(!swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.x = buttons[(int)(swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.back = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.start = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_stick_left = buttons[(int)Button.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            TriggerFunctionality GetTriggerFunctionality(string key) {
                string value = ConfigurationManager.AppSettings[key];
                // Parse the value to your ButtonFunctionality enum. Assuming you have such an enum.
                if (Enum.TryParse(value, out TriggerFunctionality result)) {
                    return result;
                }
                return TriggerFunctionality.OFF; // Default or error handling
            }

            if (Config.Value("home") != "0")
                output.guide = false;

            if (!isSnes) {
                if (other != null || isPro) { // no need for && other != this
                    output.axis_left_x = CastStickValue((other == input && !isLeft) ? stick2[0] : stick[0]);
                    output.axis_left_y = CastStickValue((other == input && !isLeft) ? stick2[1] : stick[1]);

                    output.axis_right_x = CastStickValue((other == input && !isLeft) ? stick[0] : stick2[0]);
                    output.axis_right_y = CastStickValue((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.axis_left_y = CastStickValue((isLeft ? 1 : -1) * stick[0]);
                    output.axis_left_x = CastStickValue((isLeft ? -1 : 1) * stick[1]);
                }
            }
            // MAP SL AND SR BUTTONS TO THE TRIGGER BUTTONS HERE!
            // Change the last part of the code to .OFF .SL .SR .SLO or .SRO. and make sure to close it with the ; symbol

            TriggerFunctionality triggerLeftFunctionality = GetTriggerFunctionality("SpecialHotkeyLT"); // // L TRIGGER, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF to disable
            TriggerFunctionality triggerRightFunctionality = GetTriggerFunctionality("SpecialHotkeyRT"); // // R TRIGGER, change to SR; or SL; for L joy-con, SRO; or SLO; for R joy-con or OFF to disable

            if (other != null || isPro) {
                byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                output.trigger_left = (byte)(
                    (buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_2)] || other.buttons[(int)Button.SHOULDER2_2] ? lval :
                    triggerLeftFunctionality == TriggerFunctionality.SL && buttons[(int)Button.SL] ? lval :
                    triggerLeftFunctionality == TriggerFunctionality.SR && buttons[(int)Button.SR] ? lval :
                    triggerLeftFunctionality == TriggerFunctionality.SLO && other != null && other.buttons[(int)Button.SL] ? lval :
                    triggerLeftFunctionality == TriggerFunctionality.SRO && other != null && other.buttons[(int)Button.SR] ? lval : 0)); // ZL button

                output.trigger_right = (byte)(
                    (buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval :
                    triggerRightFunctionality == TriggerFunctionality.SL && buttons[(int)Button.SL] ? rval :
                    triggerRightFunctionality == TriggerFunctionality.SR && buttons[(int)Button.SR] ? rval :
                    triggerRightFunctionality == TriggerFunctionality.SLO && other != null && other.buttons[(int)Button.SL] ? rval :
                    triggerRightFunctionality == TriggerFunctionality.SRO && other != null && other.buttons[(int)Button.SR] ? rval : 0)); // ZR button
            } else {
                output.trigger_left = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                output.trigger_right = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
            }

            return output;
        }

        public static OutputControllerDualShock4InputState MapToDualShock4Input(Joycon input) {
            var output = new OutputControllerDualShock4InputState();

            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (isPro) {
                output.cross = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.circle = buttons[(int)(!swapAB ? Button.A : Button.B)];
                output.triangle = buttons[(int)(!swapXY ? Button.X : Button.Y)];
                output.square = buttons[(int)(!swapXY ? Button.Y : Button.X)];


                if (buttons[(int)Button.DPAD_UP]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Northwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Northeast;
                    else
                        output.dPad = DpadDirection.North;
                } else if (buttons[(int)Button.DPAD_DOWN]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Southwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Southeast;
                    else
                        output.dPad = DpadDirection.South;
                } else if (buttons[(int)Button.DPAD_LEFT])
                    output.dPad = DpadDirection.West;
                else if (buttons[(int)Button.DPAD_RIGHT])
                    output.dPad = DpadDirection.East;

                output.share = buttons[(int)Button.CAPTURE];
                output.options = buttons[(int)Button.PLUS];
                output.ps = buttons[(int)Button.HOME];
                output.touchpad = buttons[(int)Button.MINUS];
                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];
                output.thumb_left = buttons[(int)Button.STICK];
                output.thumb_right = buttons[(int)Button.STICK2];
            } else {
                if (other != null) { // no need for && other != this
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];

                    if (buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Northwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Northeast;
                        else
                            output.dPad = DpadDirection.North;
                    else if (buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Southwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Southeast;
                        else
                            output.dPad = DpadDirection.South;
                    else if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                        output.dPad = DpadDirection.West;
                    else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                        output.dPad = DpadDirection.East;

                    output.share = buttons[(int)Button.CAPTURE];
                    output.options = buttons[(int)Button.PLUS];
                    output.ps = buttons[(int)Button.HOME];
                    output.touchpad = buttons[(int)Button.MINUS];
                    output.shoulder_left = buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)];
                    output.thumb_left = buttons[(int)(isLeft ? Button.STICK : Button.STICK2)];
                    output.thumb_right = buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)];
                } else { // single joycon mode
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.ps = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.options = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_left = buttons[(int)Button.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            if (Config.Value("home") != "0")
                output.ps = false;

            if (!isSnes) {
                if (other != null || isPro) { // no need for && other != this
                    output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                    output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);
                    output.thumb_right_x = CastStickValueByte((other == input && !isLeft) ? -stick[0] : -stick2[0]);
                    output.thumb_right_y = CastStickValueByte((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.thumb_left_y = CastStickValueByte((isLeft ? 1 : -1) * stick[0]);
                    output.thumb_left_x = CastStickValueByte((isLeft ? 1 : -1) * stick[1]);
                }
            }

            if (other != null || isPro) {
                byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
            } else {
                output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
            }
            // Output digital L2 / R2 in addition to analog L2 / R2
            output.trigger_left = output.trigger_left_value > 0 ? output.trigger_left = true : output.trigger_left = false;
            output.trigger_right = output.trigger_right_value > 0 ? output.trigger_right = true : output.trigger_right = false;

            return output;
        }
    }
}
