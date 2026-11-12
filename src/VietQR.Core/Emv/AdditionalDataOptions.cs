namespace VietQR.Core;

/// <summary>
/// Các trường dữ liệu bổ sung trong Tag 62 (Additional Data Field Template) theo chuẩn EMVCo và NAPAS247.
/// Phục vụ thanh toán hóa đơn, bán lẻ và đối soát IPN/Webhook tự động.
/// </summary>
public sealed class AdditionalDataOptions
{
    /// <summary>
    /// Sub-tag 01: Số hóa đơn / mã chứng từ (Bill Number - tối đa 25 ký tự).
    /// </summary>
    public string? BillNumber { get; set; }

    /// <summary>
    /// Sub-tag 05: Mã đơn hàng / mã tham chiếu giao dịch (Reference Label - tối đa 25 ký tự).
    /// Dùng cho Webhook / IPN tự động gạch nợ đơn hàng.
    /// </summary>
    public string? ReferenceLabel { get; set; }

    /// <summary>
    /// Sub-tag 08: Mục đích giao dịch / Nội dung chuyển khoản (Purpose of Transaction - tối đa 50 ký tự).
    /// </summary>
    public string? Purpose { get; set; }
}

