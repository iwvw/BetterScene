namespace BetterScene.Core.Displays;

public enum DdcFailure
{
    Unknown,
    I2cNotSupported,
    I2cDeviceMissing,
    I2cTransmit,
    I2cReceive,
    VcpNotSupported,
    InvalidData,
    InvalidTimingStatus,
    InvalidCapabilities,
    InvalidCommand,
    InvalidLength,
    InvalidChecksum,
    InvalidHandle,
    MonitorMissing,
}

public static class DdcErrorClassifier
{
    private const int ErrorGraphicsI2cNotSupported = unchecked((int)0xC0262580);
    private const int ErrorGraphicsI2cDeviceDoesNotExist = unchecked((int)0xC0262581);
    private const int ErrorGraphicsI2cErrorTransmittingData = unchecked((int)0xC0262582);
    private const int ErrorGraphicsI2cErrorReceivingData = unchecked((int)0xC0262583);
    private const int ErrorGraphicsDdcciVcpNotSupported = unchecked((int)0xC0262584);
    private const int ErrorGraphicsDdcciInvalidData = unchecked((int)0xC0262585);
    private const int ErrorGraphicsDdcciInvalidTimingStatus = unchecked((int)0xC0262586);
    private const int ErrorGraphicsMcaInvalidCapabilities = unchecked((int)0xC0262587);
    private const int ErrorGraphicsDdcciInvalidMessageCommand = unchecked((int)0xC0262589);
    private const int ErrorGraphicsDdcciInvalidMessageLength = unchecked((int)0xC026258A);
    private const int ErrorGraphicsDdcciInvalidMessageChecksum = unchecked((int)0xC026258B);
    private const int ErrorGraphicsInvalidPhysicalMonitorHandle = unchecked((int)0xC026258C);
    private const int ErrorGraphicsMonitorNoLongerExists = unchecked((int)0xC026258D);

    public static DdcFailure Classify(int error)
    {
        return error switch
        {
            ErrorGraphicsI2cNotSupported => DdcFailure.I2cNotSupported,
            ErrorGraphicsI2cDeviceDoesNotExist => DdcFailure.I2cDeviceMissing,
            ErrorGraphicsI2cErrorTransmittingData => DdcFailure.I2cTransmit,
            ErrorGraphicsI2cErrorReceivingData => DdcFailure.I2cReceive,
            ErrorGraphicsDdcciVcpNotSupported => DdcFailure.VcpNotSupported,
            ErrorGraphicsDdcciInvalidData => DdcFailure.InvalidData,
            ErrorGraphicsDdcciInvalidTimingStatus => DdcFailure.InvalidTimingStatus,
            ErrorGraphicsMcaInvalidCapabilities => DdcFailure.InvalidCapabilities,
            ErrorGraphicsDdcciInvalidMessageCommand => DdcFailure.InvalidCommand,
            ErrorGraphicsDdcciInvalidMessageLength => DdcFailure.InvalidLength,
            ErrorGraphicsDdcciInvalidMessageChecksum => DdcFailure.InvalidChecksum,
            ErrorGraphicsInvalidPhysicalMonitorHandle => DdcFailure.InvalidHandle,
            ErrorGraphicsMonitorNoLongerExists => DdcFailure.MonitorMissing,
            _ => DdcFailure.Unknown,
        };
    }

    public static string Describe(int error)
    {
        DdcFailure failure = Classify(error);
        string text = failure switch
        {
            DdcFailure.I2cNotSupported => "显示器不支持 I2C/DDC",
            DdcFailure.I2cDeviceMissing => "I2C 设备不存在",
            DdcFailure.I2cTransmit => "I2C 发送失败",
            DdcFailure.I2cReceive => "I2C 接收失败",
            DdcFailure.VcpNotSupported => "不支持该 VCP 功能",
            DdcFailure.InvalidData => "DDC 数据无效",
            DdcFailure.InvalidTimingStatus => "显示器返回无效时序",
            DdcFailure.InvalidCapabilities => "能力字符串无效",
            DdcFailure.InvalidCommand => "DDC 命令无效",
            DdcFailure.InvalidLength => "DDC 报文长度无效",
            DdcFailure.InvalidChecksum => "DDC 校验和错误",
            DdcFailure.InvalidHandle => "显示器句柄无效",
            DdcFailure.MonitorMissing => "显示器已断开",
            _ => "未知错误",
        };

        return $"{text} (0x{error:X8})";
    }

    public static bool IsTransient(DdcFailure failure) => failure is
        DdcFailure.I2cTransmit or
        DdcFailure.I2cReceive or
        DdcFailure.InvalidData or
        DdcFailure.InvalidTimingStatus or
        DdcFailure.InvalidChecksum;

    public static bool IsFatal(DdcFailure failure) => failure is
        DdcFailure.I2cNotSupported or
        DdcFailure.I2cDeviceMissing or
        DdcFailure.InvalidHandle or
        DdcFailure.MonitorMissing;
}
