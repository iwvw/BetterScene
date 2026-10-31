using System.Text.Json;
using System.Text.Json.Nodes;
using BetterScene.Core.Control;
using BetterScene.Core.Displays;

namespace BetterScene.Core.Tests;

public class ControlArgsTests
{
    [Fact]
    public void RequireString_reads_plain_string()
    {
        var args = new JsonObject { ["display"] = "HWV6246" };
        Assert.Equal("HWV6246", args.RequireString("display"));
    }

    [Fact]
    public void OptionalString_returns_null_for_missing()
    {
        Assert.Null(new JsonObject().OptionalString("display"));
    }

    [Fact]
    public void OptionalString_treats_blank_as_missing()
    {
        var args = new JsonObject { ["display"] = "   " };
        Assert.Null(args.OptionalString("display"));
    }

    [Fact]
    public void OptionalString_accepts_number()
    {
        var args = new JsonObject { ["display"] = 3 };
        Assert.Equal("3", args.OptionalString("display"));
    }

    [Fact]
    public void RequireInt_reads_number()
    {
        var args = new JsonObject { ["value"] = 42 };
        Assert.Equal(42, args.RequireInt("value"));
    }

    [Fact]
    public void RequireInt_reads_numeric_string()
    {
        var args = new JsonObject { ["value"] = "42" };
        Assert.Equal(42, args.RequireInt("value"));
    }

    [Fact]
    public void RequireInt_throws_on_missing()
    {
        var ex = Assert.Throws<ControlException>(() => new JsonObject().RequireInt("value"));
        Assert.Equal(ControlErrorCodes.BadRequest, ex.Code);
    }

    [Fact]
    public void RequirePercent_accepts_bounds()
    {
        Assert.Equal(0, new JsonObject { ["value"] = 0 }.RequirePercent("value"));
        Assert.Equal(100, new JsonObject { ["value"] = 100 }.RequirePercent("value"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void RequirePercent_rejects_out_of_range(int value)
    {
        var ex = Assert.Throws<ControlException>(
            () => new JsonObject { ["value"] = value }.RequirePercent("value"));

        Assert.Equal(ControlErrorCodes.BadRequest, ex.Code);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void RequireBool_accepts_common_spellings(string raw, bool expected)
    {
        var args = new JsonObject { ["on"] = raw };
        Assert.Equal(expected, args.RequireBool("on"));
    }

    [Fact]
    public void RequireBool_accepts_json_boolean()
    {
        Assert.True(new JsonObject { ["on"] = true }.RequireBool("on"));
        Assert.False(new JsonObject { ["on"] = false }.RequireBool("on"));
    }

    [Theory]
    [InlineData("0x10", 0x10)]
    [InlineData("0xdf", 0xDF)]
    [InlineData("16", 16)]
    [InlineData("0xFF", 0xFF)]
    public void RequireByte_accepts_hex_and_decimal(string raw, byte expected)
    {
        var args = new JsonObject { ["code"] = raw };
        Assert.Equal(expected, args.RequireByte("code"));
    }

    [Fact]
    public void RequireByte_rejects_out_of_range()
    {
        var ex = Assert.Throws<ControlException>(
            () => new JsonObject { ["code"] = "0x100" }.RequireByte("code"));

        Assert.Equal(ControlErrorCodes.BadRequest, ex.Code);
    }

    [Theory]
    [InlineData("0x0F", 0x0F)]
    [InlineData("15", 15)]
    [InlineData("0xFFFFFFFF", uint.MaxValue)]
    public void RequireUInt_accepts_hex_and_decimal(string raw, uint expected)
    {
        var args = new JsonObject { ["value"] = raw };
        Assert.Equal(expected, args.RequireUInt("value"));
    }

    [Fact]
    public void Require_returns_empty_object_for_null()
    {
        JsonObject? args = null;
        Assert.NotNull(args.Require());
    }
}

public class ControlProtocolTests
{
    [Fact]
    public void Request_round_trips()
    {
        var request = new ControlRequest
        {
            Id = "abc",
            Cmd = "brightness.set",
            Args = new JsonObject { ["display"] = "*", ["value"] = 35 },
        };

        string json = JsonSerializer.Serialize(request, ControlProtocol.Json);
        ControlRequest? back = JsonSerializer.Deserialize<ControlRequest>(json, ControlProtocol.Json);

        Assert.NotNull(back);
        Assert.Equal("abc", back.Id);
        Assert.Equal("brightness.set", back.Cmd);
        Assert.Equal(35, back.Args!.RequireInt("value"));
    }

    [Fact]
    public void Request_defaults_to_current_protocol_version()
    {
        var request = new ControlRequest();
        Assert.Equal(ControlProtocol.Version, request.V);
    }

    [Fact]
    public void Response_serializes_ok_payload()
    {
        var response = ControlResponse.Success("id1", new JsonObject { ["theme"] = "dark" });
        string json = JsonSerializer.Serialize(response, ControlProtocol.Json);

        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"theme\":\"dark\"", json);
        Assert.DoesNotContain("error", json);
    }

    [Fact]
    public void Response_serializes_error_payload()
    {
        var response = ControlResponse.Fail("id2", ControlErrorCodes.DisplayNotFound, "找不到显示器");
        string json = JsonSerializer.Serialize(response, ControlProtocol.Json);

        Assert.Contains("\"ok\":false", json);
        Assert.Contains(ControlErrorCodes.DisplayNotFound, json);
    }

    [Fact]
    public void Response_omits_null_result()
    {
        var response = ControlResponse.Fail("id3", ControlErrorCodes.Internal, "boom");
        string json = JsonSerializer.Serialize(response, ControlProtocol.Json);

        Assert.DoesNotContain("\"result\"", json);
    }

    [Fact]
    public void Pipe_name_carries_session_and_version()
    {
        string name = ControlProtocol.PipeNameFor(3);

        Assert.Contains("BetterScene.Control", name);
        Assert.Contains($".v{ControlProtocol.Version}", name);
        Assert.EndsWith(".3", name);
    }

    [Fact]
    public void Json_keeps_chinese_readable()
    {
        string json = JsonSerializer.Serialize(
            new ControlError { Code = "x", Message = "找不到显示器" },
            ControlProtocol.Json);

        Assert.Contains("找不到显示器", json);
    }
}

public class DisplaySelectorTests
{
    private static DisplayTarget Make(
        string id,
        string gdi,
        string firmware,
        string model,
        bool primary = false,
        DisplayBackend backend = DisplayBackend.Ddc) => new()
        {
            Id = id,
            GdiName = gdi,
            Description = model,
            FirmwareId = firmware,
            Model = model,
            Vendor = "DEL",
            IsPrimary = primary,
            Backend = backend,
            Width = 2560,
            Height = 1440,
            Brightness = 50,
        };

    private static List<DisplayTarget> Two() =>
    [
        Make(@"\\?\DISPLAY#DEL41D9#5&abcd&0&UID4352", @"\\.\DISPLAY1", "DEL41D9", "S2719DGF", primary: true),
        Make(@"\\?\DISPLAY#BOE0C8E#4&efgh&0&UID1", @"\\.\DISPLAY2", "BOE0C8E", "NE160QDM"),
    ];

    [Fact]
    public void Empty_selector_returns_all()
    {
        Assert.Equal(2, DisplaySelector.Resolve(Two(), null).Count);
        Assert.Equal(2, DisplaySelector.Resolve(Two(), "").Count);
    }

    [Fact]
    public void Star_returns_all()
    {
        Assert.Equal(2, DisplaySelector.Resolve(Two(), "*").Count);
    }

    [Fact]
    public void Primary_resolves_to_primary_display()
    {
        List<DisplayTarget> hit = DisplaySelector.Resolve(Two(), "primary");

        Assert.Single(hit);
        Assert.Equal("DEL41D9", hit[0].FirmwareId);
    }

    [Fact]
    public void Resolves_by_full_id()
    {
        List<DisplayTarget> hit = DisplaySelector.Resolve(Two(), @"\\?\DISPLAY#BOE0C8E#4&efgh&0&UID1");

        Assert.Single(hit);
        Assert.Equal("BOE0C8E", hit[0].FirmwareId);
    }

    [Fact]
    public void Resolves_by_gdi_name()
    {
        List<DisplayTarget> hit = DisplaySelector.Resolve(Two(), @"\\.\DISPLAY2");

        Assert.Single(hit);
        Assert.Equal("BOE0C8E", hit[0].FirmwareId);
    }

    [Fact]
    public void Resolves_by_firmware_id()
    {
        List<DisplayTarget> hit = DisplaySelector.Resolve(Two(), "BOE0C8E");

        Assert.Single(hit);
        Assert.Equal(@"\\.\DISPLAY2", hit[0].GdiName);
    }

    [Fact]
    public void Resolves_by_model()
    {
        List<DisplayTarget> hit = DisplaySelector.Resolve(Two(), "S2719DGF");

        Assert.Single(hit);
        Assert.Equal("DEL41D9", hit[0].FirmwareId);
    }

    [Fact]
    public void Firmware_match_is_case_insensitive()
    {
        List<DisplayTarget> hit = DisplaySelector.Resolve(Two(), "boe0c8e");
        Assert.Single(hit);
    }

    [Fact]
    public void Unknown_selector_throws_with_available_list()
    {
        var ex = Assert.Throws<ControlException>(() => DisplaySelector.Resolve(Two(), "NOPE"));

        Assert.Equal(ControlErrorCodes.DisplayNotFound, ex.Code);
        Assert.Contains("DEL41D9", ex.Message);
        Assert.Contains("BOE0C8E", ex.Message);
    }

    [Fact]
    public void Ambiguous_firmware_match_throws()
    {
        List<DisplayTarget> targets =
        [
            Make("id1", @"\\.\DISPLAY1", "SAME01", "A"),
            Make("id2", @"\\.\DISPLAY2", "SAME01", "B"),
        ];

        var ex = Assert.Throws<ControlException>(() => DisplaySelector.Resolve(targets, "SAME01"));
        Assert.Equal(ControlErrorCodes.DisplayAmbiguous, ex.Code);
    }

    [Fact]
    public void ResolveControllable_rejects_gamma_only_when_no_backend()
    {
        List<DisplayTarget> targets = [Make("id1", @"\\.\DISPLAY1", "F1", "A", backend: DisplayBackend.None)];

        var ex = Assert.Throws<ControlException>(
            () => DisplaySelector.ResolveControllable(targets, "*"));

        Assert.Equal(ControlErrorCodes.Unsupported, ex.Code);
    }

    [Fact]
    public void ResolveControllable_filters_out_none_backend()
    {
        List<DisplayTarget> targets =
        [
            Make("id1", @"\\.\DISPLAY1", "F1", "A", backend: DisplayBackend.Ddc),
            Make("id2", @"\\.\DISPLAY2", "F2", "B", backend: DisplayBackend.None),
        ];

        List<DisplayTarget> hit = DisplaySelector.ResolveControllable(targets, "*");

        Assert.Single(hit);
        Assert.Equal("F1", hit[0].FirmwareId);
    }

    [Fact]
    public void ToState_serializes_expected_fields()
    {
        DisplayTarget target = Make("id1", @"\\.\DISPLAY1", "F1", "A", primary: true);

        JsonObject state = DisplaySelector.ToState(
            target,
            brightness: 42,
            rangeLow: 10,
            rangeHigh: 90,
            new CurrentDisplayMode(2560, 1440, 165),
            scale: 125,
            new HdrState(true, true, false, false, 10, "HDR10"));

        Assert.Equal(42, state["brightness"]!.GetValue<int>());
        Assert.Equal(10, state["rangeLow"]!.GetValue<int>());
        Assert.Equal(165, state["refreshRate"]!.GetValue<int>());
        Assert.Equal(125, state["scale"]!.GetValue<int>());
        Assert.True(state["hdr"]!.GetValue<bool>());
        Assert.True(state["isPrimary"]!.GetValue<bool>());
        Assert.Equal("ddc", state["backend"]!.GetValue<string>());
    }

    [Fact]
    public void ToState_falls_back_to_target_mode_when_unreadable()
    {
        DisplayTarget target = Make("id1", @"\\.\DISPLAY1", "F1", "A");

        JsonObject state = DisplaySelector.ToState(
            target, 50, 0, 100, mode: null, scale: null, HdrState.Unknown);

        Assert.Equal(2560, state["width"]!.GetValue<int>());
        Assert.Equal(1440, state["height"]!.GetValue<int>());
        Assert.Null(state["scale"]);
        Assert.False(state["hdr"]!.GetValue<bool>());
    }
}
