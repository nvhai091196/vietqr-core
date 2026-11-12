using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace VietQR.Core;

/// <summary>
/// Loại dịch vụ chuyển tiền NAPAS247 (theo mục 5.2.3.2.3 tài liệu NAPAS)
/// </summary>
public enum VietQrServiceType
{
    /// <summary>
    /// QRIBFTTA: Dịch vụ chuyển nhanh NAPAS247 đến Tài khoản
    /// </summary>
    Account,

    /// <summary>
    /// QRIBFTTC: Dịch vụ chuyển nhanh NAPAS247 đến Thẻ
    /// </summary>
    Card
}

/// <summary>
/// Loại mã VietQR theo chuẩn khởi tạo NAPAS247 / EMVCo (Tag 01 Point of Initiation Method):
/// - Static: Mã QR tĩnh ("11") - khách quét tự nhập số tiền khi thanh toán.
/// - Dynamic: Mã QR động ("12") - mã gắn số tiền thanh toán cố định cho đơn hàng/giao dịch.
/// </summary>
public enum VietQrType
{
    /// <summary>
    /// Mã QR Tĩnh (Tag 01 = "11"): Dùng nhiều lần, không gắn cứng số tiền (Mục 6.1.1, 6.1.2 NAPAS).
    /// </summary>
    Static,

    /// <summary>
    /// Mã QR Động (Tag 01 = "12"): Dùng theo từng giao dịch, gắn sẵn số tiền thanh toán (Mục 6.1.3, 6.1.4 NAPAS).
    /// </summary>
    Dynamic
}

/// <summary>
/// Core tạo chuỗi mã thanh toán VietQR (chuẩn EMVCo / NAPAS247).
/// Định dạng mã VietQR của NAPAS (QR_Format_T&amp;C_v1.0_VN_092021.pdf).
/// </summary>
public class VietQr : IQrGenerator
{
    public string Generate(VietQrRequest request) =>
        VietQrPayload.Create(
            request.Bin,
            request.AccountNo,
            request.EffectiveQrType,
            request.ServiceType,
            request.Amount,
            request.Description,
            request.BillNumber);

    // Tag 00: Payload Format Indicator (Mục 5.2.1 - Giá trị cố định "01")
    public const string DefaultPayloadFormatIndicator = "000201";

    // Tag 01: Point of Initiation Method (Mục 5.2.2 - "11": QR tĩnh, "12": QR động)
    public const string MethodStatic = "010211";
    public const string MethodDynamic = "010212";

    // Tag 38: Thông tin định danh người thụ hưởng (Mục 5.2.3.2)
    public const string NapasAid = "0010A000000727"; // GUID của NAPAS (Mục 5.2.3.2.1)
    public const string ServiceCodeAccount = "0208QRIBFTTA"; // Chuyển nhanh đến Tài khoản
    public const string ServiceCodeCard = "0208QRIBFTTC";    // Chuyển nhanh đến Thẻ

    // Tag 53: Mã tiền tệ VND = "704" (Mục 5.2.5 - ISO 4217)
    public const string DefaultCurrency = "5303704";

    // Tag 58: Mã quốc gia VN = "VN" (Mục 5.2.10 - ISO 3166-1 alpha 2)
    public const string DefaultCountry = "5802VN";

    // Bảng cache 100 chuỗi độ dài "00".."99" giúp chuyển đổi độ dài không cấp phát bộ nhớ (0 byte allocation)
    private static readonly string[] TwoDigitLookup = Enumerable.Range(0, 100).Select(i => i.ToString("D2")).ToArray();

    private string _pointOfInitiationMethod = MethodStatic;
    private string _consumerAccountInformation = "";
    private string _transactionAmount = "";
    private string _merchantName = "";
    private string _merchantCity = "";
    private string _additionalDataFieldTemplate = "";
    private bool _manualMethodOverride = false;

    /// <summary>
    /// Chuyển đổi số nguyên thành chuỗi 2 chữ số (VD: 5 -> "05", 25 -> "25"). Đảm bảo chuẩn TLV EMVCo (01-99).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ConvertLength(int length)
    {
        if ((uint)length < 100) return TwoDigitLookup[length];
        throw new ArgumentOutOfRangeException(nameof(length), $"Độ dài trường TLV ({length}) vượt quá giới hạn tối đa 99 ký tự của chuẩn EMVCo/NAPAS.");
    }

    /// <summary>
    /// Loại bỏ dấu tiếng Việt thành ký tự không dấu chuẩn ngân hàng và lọc ký tự điều khiển (Tối ưu Fast-Path cho chuỗi ASCII)
    /// </summary>
    public static string RemoveDiacritics(string text) => DiacriticsNormalizer.Normalize(text);

    /// <summary>
    /// Chọn phương thức khởi tạo thủ công ("11" = Tĩnh, "12" = Động)
    /// </summary>
    public VietQr SetPointOfInitiationMethod(bool isDynamic)
    {
        _pointOfInitiationMethod = isDynamic ? MethodDynamic : MethodStatic;
        _manualMethodOverride = true;
        return this;
    }

    /// <summary>
    /// Chọn phương thức khởi tạo thủ công theo VietQrType (Static = "11", Dynamic = "12").
    /// </summary>
    public VietQr SetPointOfInitiationMethod(VietQrType qrType) =>
        SetPointOfInitiationMethod(qrType == VietQrType.Dynamic);

    /// <summary>
    /// Thiết lập số tiền giao dịch (Tag 54 - Tối đa 13 chữ số, mục 5.2.6).
    /// Chuẩn VietQR: Chỉ chấp nhận số nguyên dương, khác 0, không có dấu chấm/phẩy.
    /// Nếu có số tiền, QR sẽ tự động chuyển sang phương thức động ("12").
    /// </summary>
    public VietQr SetTransactionAmount(string? money)
    {
        if (string.IsNullOrWhiteSpace(money))
        {
            _transactionAmount = "";
            if (!_manualMethodOverride) _pointOfInitiationMethod = MethodStatic;
            return this;
        }

        var trimmed = money.Trim();
        if (!trimmed.All(char.IsDigit) || trimmed.Length > 13 || !ulong.TryParse(trimmed, out var val) || val == 0)
        {
            throw new ArgumentException("Invalid amount - Trường chỉ nhập số, tối đa 13 kí tự", nameof(money));
        }

        var clean = val.ToString();
        var length = ConvertLength(clean.Length);
        _transactionAmount = $"54{length}{clean}";

        // Tự động chuyển thành QR động (Mục 5.2.2)
        if (!_manualMethodOverride) _pointOfInitiationMethod = MethodDynamic;
        return this;
    }

    /// <summary>
    /// Thiết lập thông tin tổ chức thụ hưởng (Tag 38 - Mục 5.2.3.2):
    /// - Sub-ID 00: BNB ID / Acquirer ID (Mã BIN ngân hàng - đúng 6 số)
    /// - Sub-ID 01: Consumer ID (Số tài khoản hoặc Số thẻ - tối đa 25 ký tự, hỗ trợ tài khoản định danh/ảo)
    /// - Sub-ID 02: Mã dịch vụ QRIBFTTA (Tài khoản) hoặc QRIBFTTC (Thẻ)
    /// </summary>
    public VietQr SetBeneficiaryOrganization(
        string acquirerId,
        string consumerId,
        VietQrServiceType serviceType = VietQrServiceType.Account)
    {
        var acquirerTrimmed = acquirerId?.Trim() ?? "";
        var consumerTrimmed = consumerId?.Trim() ?? "";

        if (acquirerTrimmed.Length != 6 || !acquirerTrimmed.All(char.IsDigit))
        {
            throw new ArgumentException("Mã ngân hàng (acquirerId / bankBin) bắt buộc phải là đúng 6 chữ số.", nameof(acquirerId));
        }

        if (string.IsNullOrEmpty(consumerTrimmed) || consumerTrimmed.Length > 25)
        {
            throw new ArgumentException($"Số tài khoản/thẻ (consumerId / accountNo) không được rỗng và tối đa 25 ký tự theo chuẩn NAPAS / Virtual Account (nhận được: {consumerTrimmed.Length} ký tự).", nameof(consumerId));
        }

        var sub00 = $"00{ConvertLength(acquirerTrimmed.Length)}{acquirerTrimmed}";
        var sub01 = $"01{ConvertLength(consumerTrimmed.Length)}{consumerTrimmed}";
        var sub01Total = $"{sub00}{sub01}";

        var beneficiaryOrg = $"01{ConvertLength(sub01Total.Length)}{sub01Total}";
        var serviceCode = serviceType == VietQrServiceType.Card ? ServiceCodeCard : ServiceCodeAccount;

        var tag38Value = $"{NapasAid}{beneficiaryOrg}{serviceCode}";
        _consumerAccountInformation = $"38{ConvertLength(tag38Value.Length)}{tag38Value}";
        return this;
    }

    /// <summary>
    /// Thiết lập các trường dữ liệu bổ sung trong Tag 62 (Additional Data Field Template) theo chuẩn NAPAS247 và EMVCo.
    /// </summary>
    public VietQr SetAdditionalData(AdditionalDataOptions? options)
    {
        if (options == null)
        {
            _additionalDataFieldTemplate = "";
            return this;
        }

        return SetAdditionalData(
            purpose: options.Purpose,
            billNumber: options.BillNumber,
            referenceLabel: options.ReferenceLabel);
    }

    /// <summary>
    /// Thiết lập thông tin bổ sung (Tag 62 - Mục 5.2.14) theo chuẩn NAPAS247:
    /// - Sub-ID 01: Số hóa đơn (Bill Number - tối đa 25 ký tự)
    /// - Sub-ID 05: Mã đơn hàng tham chiếu / Order ID (Reference Label - tối đa 25 ký tự)
    /// - Sub-ID 08: Mục đích giao dịch / Nội dung thanh toán (Purpose - tối đa 50 ký tự)
    /// Đảm bảo tổng độ dài dữ liệu trong Tag 62 (ID 62) không vượt quá 99 ký tự theo chuẩn NAPAS.
    /// </summary>
    public VietQr SetAdditionalData(
        string? purpose = null,
        string? billNumber = null,
        string? referenceLabel = null)
    {
        const int MaxTag62Length = 99;
        var innerSb = new StringBuilder();

        void AppendSubTag(string id, string? val, int maxLen = 25)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            var clean = RemoveDiacritics(val.Trim());
            if (string.IsNullOrEmpty(clean)) return;

            int remainingSpace = MaxTag62Length - innerSb.Length - 4;
            if (remainingSpace <= 0) return;

            int limit = Math.Min(maxLen, remainingSpace);
            if (clean.Length > limit) clean = clean[..limit];
            innerSb.Append($"{id}{ConvertLength(clean.Length)}{clean}");
        }

        // Sub-tag 01: Bill Number
        if (!string.IsNullOrWhiteSpace(billNumber))
        {
            AppendSubTag("01", billNumber, 25);
            if (!_manualMethodOverride) _pointOfInitiationMethod = MethodDynamic;
        }

        // Sub-tag 05: Reference Label (Order ID)
        if (!string.IsNullOrWhiteSpace(referenceLabel))
        {
            AppendSubTag("05", referenceLabel, 25);
            if (!_manualMethodOverride) _pointOfInitiationMethod = MethodDynamic;
        }

        // Sub-tag 08: Purpose of Transaction (Nội dung CK - tối đa 50 ký tự)
        AppendSubTag("08", purpose, 50);

        var inner = innerSb.ToString();
        _additionalDataFieldTemplate = inner.Length > 0 ? $"62{ConvertLength(inner.Length)}{inner}" : "";
        return this;
    }

    /// <summary>
    /// Thiết lập thông tin điểm bán / đơn vị chấp nhận thanh toán (Tag 59 Merchant Name &amp; Tag 60 Merchant City).
    /// Tự động chuẩn hóa chữ HOA không dấu theo quy định NAPAS247.
    /// </summary>
    /// <param name="name">Tên điểm bán / tên chủ tài khoản (Tag 59 - tối đa 25 ký tự).</param>
    /// <param name="city">Tên thành phố / địa phương (Tag 60 - tối đa 15 ký tự, mặc định "HANOI").</param>
    public VietQr SetMerchantInfo(string? name, string? city = "HANOI")
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var n = RemoveDiacritics(name.Trim()).ToUpperInvariant();
            if (n.Length > 25) n = n[..25];
            _merchantName = $"59{ConvertLength(n.Length)}{n}";
        }
        else
        {
            _merchantName = "";
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            var c = RemoveDiacritics(city.Trim()).ToUpperInvariant();
            if (c.Length > 15) c = c[..15];
            _merchantCity = $"60{ConvertLength(c.Length)}{c}";
        }
        else
        {
            _merchantCity = "";
        }

        return this;
    }

    /// <summary>
    /// Alias tương thích ngược với Flutter project: gán mục đích giao dịch
    /// </summary>
    public VietQr SetAdditionalDataFieldTemplate(string? content)
    {
        return SetAdditionalData(purpose: content);
    }

    // Bảng tra CRC16-CCITT chuẩn ISO/IEC 13239 (Đa thức 0x1021, Khởi tạo 0xFFFF, Mục 5.2.15).
    // Sử dụng C# 12 ReadOnlySpan nhúng trực tiếp vào phân vùng rdata của DLL, không cấp phát heap.
    private static readonly ushort[] s_crcTable = new ushort[]
    {
        0x0000, 0x1021, 0x2042, 0x3063, 0x4084, 0x50a5, 0x60c6, 0x70e7,
        0x8108, 0x9129, 0xa14a, 0xb16b, 0xc18c, 0xd1ad, 0xe1ce, 0xf1ef,
        0x1231, 0x0210, 0x3273, 0x2252, 0x52b5, 0x4294, 0x72f7, 0x62d6,
        0x9339, 0x8318, 0xb37b, 0xa35a, 0xd3bd, 0xc39c, 0xf3ff, 0xe3de,
        0x2462, 0x3443, 0x0420, 0x1401, 0x64e6, 0x74c7, 0x44a4, 0x5485,
        0xa56a, 0xb54b, 0x8528, 0x9509, 0xe5ee, 0xf5cf, 0xc5ac, 0xd58d,
        0x3653, 0x2672, 0x1611, 0x0630, 0x76d7, 0x66f6, 0x5695, 0x46b4,
        0xb75b, 0xa77a, 0x9719, 0x8738, 0xf7df, 0xe7fe, 0xd79d, 0xc7bc,
        0x48c4, 0x58e5, 0x6886, 0x78a7, 0x0840, 0x1861, 0x2802, 0x3823,
        0xc9cc, 0xd9ed, 0xe98e, 0xf9af, 0x8948, 0x9969, 0xa90a, 0xb92b,
        0x5af5, 0x4ad4, 0x7ab7, 0x6a96, 0x1a71, 0x0a50, 0x3a33, 0x2a12,
        0xdbfd, 0xcbdc, 0xfbbf, 0xeb9e, 0x9b79, 0x8b58, 0xbb3b, 0xab1a,
        0x6ca6, 0x7c87, 0x4ce4, 0x5cc5, 0x2c22, 0x3c03, 0x0c60, 0x1c41,
        0xedae, 0xfd8f, 0xcdec, 0xddcd, 0xad2a, 0xbd0b, 0x8d68, 0x9d49,
        0x7e97, 0x6eb6, 0x5ed5, 0x4ef4, 0x3e13, 0x2e32, 0x1e51, 0x0e70,
        0xff9f, 0xefbe, 0xdfdd, 0xcffc, 0xbf1b, 0xaf3a, 0x9f59, 0x8f78,
        0x9188, 0x81a9, 0xb1ca, 0xa1eb, 0xd10c, 0xc12d, 0xf14e, 0xe16f,
        0x1080, 0x00a1, 0x30c2, 0x20e3, 0x5004, 0x4025, 0x7046, 0x6067,
        0x83b9, 0x9398, 0xa3fb, 0xb3da, 0xc33d, 0xd31c, 0xe37f, 0xf35e,
        0x02b1, 0x1290, 0x22f3, 0x32d2, 0x4235, 0x5214, 0x6277, 0x7256,
        0xb5ea, 0xa5cb, 0x95a8, 0x8589, 0xf56e, 0xe54f, 0xd52c, 0xc50d,
        0x34e2, 0x24c3, 0x14a0, 0x0481, 0x7466, 0x6447, 0x5424, 0x4405,
        0xa7db, 0xb7fa, 0x8799, 0x97b8, 0xe75f, 0xf77e, 0xc71d, 0xd73c,
        0x26d3, 0x36f2, 0x0691, 0x16b0, 0x6657, 0x7676, 0x4615, 0x5634,
        0xd94c, 0xc96d, 0xf90e, 0xe92f, 0x99c8, 0x89e9, 0xb98a, 0xa9ab,
        0x5844, 0x4865, 0x7806, 0x6827, 0x18c0, 0x08e1, 0x3882, 0x28a3,
        0xcb7d, 0xdb5c, 0xeb3f, 0xfb1e, 0x8bf9, 0x9bd8, 0xabbb, 0xbb9a,
        0x4a75, 0x5a54, 0x6a37, 0x7a16, 0x0af1, 0x1ad0, 0x2ab3, 0x3a92,
        0xfd2e, 0xed0f, 0xdd6c, 0xcd4d, 0xbdaa, 0xad8b, 0x9de8, 0x8dc9,
        0x7c26, 0x6c07, 0x5c64, 0x4c45, 0x3ca2, 0x2c83, 0x1ce0, 0x0cc1,
        0xef1f, 0xff3e, 0xcf5d, 0xdf7c, 0xaf9b, 0xbfba, 0x8fd9, 0x9ff8,
        0x6e17, 0x7e36, 0x4e55, 0x5e74, 0x2e93, 0x3eb2, 0x0ed1, 0x1ef0
    };

    internal static ReadOnlySpan<ushort> CrcTable => s_crcTable;

    /// <summary>
    /// Tính giá trị kiểm thử CRC16-CCITT theo ISO/IEC 13239 trên ReadOnlySpan
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalcCRC(ReadOnlySpan<char> span)
    {
        var table = CrcTable;
        ushort crc = 0xFFFF;
        for (int i = 0; i < span.Length; i++)
        {
            int c = span[i];
            crc = (ushort)(table[((crc >> 8) ^ c) & 0xFF] ^ (crc << 8));
        }
        return crc;
    }

    /// <summary>
    /// Tính giá trị kiểm thử CRC16-CCITT theo ISO/IEC 13239
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalcCRC(string str) => string.IsNullOrEmpty(str) ? 0xFFFF : CalcCRC(str.AsSpan());

    /// <summary>
    /// Sinh mã CRC dạng chuỗi Hex 4 ký tự viết hoa (VD: "F4E5", "2E2E")
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GenCRCCode(string content) => CalcCRC(content.AsSpan()).ToString("X4");

    /// <summary>
    /// Kiểm tra tính toàn vẹn của chuỗi mã QR bất kỳ theo chuẩn NAPAS/EMVCo (Zero-allocation).
    /// Chuỗi hợp lệ bắt buộc phải có độ dài tối thiểu 8 ký tự và kết thúc bằng Tag 6304XXXX.
    /// </summary>
    public static bool VerifyCRC(string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length < 8) return false;

        var span = content.AsSpan();

        // Chuẩn NAPAS Mục 5.2.15: Mã CRC 4 ký tự cuối bắt buộc phải nằm sau định danh Tag "6304"
        if (!span[^8..^4].SequenceEqual("6304")) return false;

        var checkContent = span[..^4];
        var expectedCrc = CalcCRC(checkContent);

        if (ushort.TryParse(span[^4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedCrc))
        {
            return parsedCrc == expectedCrc;
        }

        return false;
    }

    /// <summary>
    /// Trích xuất mã BIN ngân hàng (6 chữ số) từ chuỗi EMVCo VietQR (Tag 38 -> Sub-tag 01 -> Sub-tag 00)
    /// </summary>
    public static string? ExtractBinFromEmvCo(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        // Ưu tiên dùng VietQrParser chuẩn TLV (chống rủi ro substring collision trong Tag 62)
        var parseResult = VietQrParser.Parse(content, new VietQrParseOptions { StrictCrc = false });
        if (parseResult.IsSuccess && parseResult.Value != null && !string.IsNullOrEmpty(parseResult.Value.BankBin))
        {
            return parseResult.Value.BankBin;
        }

        // Fallback logic tìm chuỗi
        int aidIdx = content.IndexOf("A000000727", StringComparison.OrdinalIgnoreCase);
        if (aidIdx >= 0)
        {
            int sub00Idx = content.IndexOf("0006", aidIdx + 10, StringComparison.Ordinal);
            if (sub00Idx >= 0 && sub00Idx + 10 <= content.Length)
            {
                var candidate = content.Substring(sub00Idx + 4, 6);
                if (candidate.All(char.IsDigit)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Đóng gói chuỗi mã VietQR hoàn chỉnh chuẩn EMVCo / NAPAS247
    /// </summary>
    public string Build()
    {
        string contentQR = string.Concat(
            DefaultPayloadFormatIndicator,
            _pointOfInitiationMethod,
            _consumerAccountInformation,
            DefaultCurrency,
            _transactionAmount,
            DefaultCountry,
            _merchantName,
            _merchantCity,
            _additionalDataFieldTemplate,
            "6304");

        string crcValue = CalcCRC(contentQR.AsSpan()).ToString("X4");
        return string.Concat(contentQR, crcValue);
    }
}
