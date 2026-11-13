using System.Runtime.InteropServices;

namespace Watcher.Capture;

// 0.14.3: минимальный COM-интерфейс UI Automation (UIAutomationClient.h) — только
// то, что нужно BrowserSite: элемент окна, поиск поля ввода, его значение и фокус.
// Без WPF (System.Windows.Automation тянет WindowsDesktop) и без пакетов. COM
// вызывает по номеру слота таблицы методов, поэтому до нужных методов —
// заглушки в том же порядке, что в заголовке Windows SDK; их не вызываем.
internal static class Uia
{
    public static readonly Guid CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    public const int TreeScopeDescendants = 4;
    public const int ControlTypePropertyId = 30003;
    public const int HasKeyboardFocusPropertyId = 30008;
    public const int AutomationIdPropertyId = 30011;
    public const int ValueValuePropertyId = 30045;
    public const int EditControlTypeId = 50004;
}

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void CompareElements();
    void CompareRuntimeIds();
    void GetRootElement();
    [PreserveSig] int ElementFromHandle(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);
    void ElementFromPoint();
    void GetFocusedElement();
    void GetRootElementBuildCache();
    void ElementFromHandleBuildCache();
    void ElementFromPointBuildCache();
    void GetFocusedElementBuildCache();
    void CreateTreeWalker();
    void GetControlViewWalker();
    void GetContentViewWalker();
    void GetRawViewWalker();
    void GetRawViewCondition();
    void GetControlViewCondition();
    void GetContentViewCondition();
    void CreateCacheRequest();
    void CreateTrueCondition();
    void CreateFalseCondition();
    [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationCondition condition);
    void CreatePropertyConditionEx();
    [PreserveSig] int CreateAndCondition([MarshalAs(UnmanagedType.Interface)] IUIAutomationCondition c1, [MarshalAs(UnmanagedType.Interface)] IUIAutomationCondition c2, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationCondition condition);
}

[ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition
{
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId();
    [PreserveSig] int FindFirst(int scope, [MarshalAs(UnmanagedType.Interface)] IUIAutomationCondition condition, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? found);
    void FindAll();
    void FindFirstBuildCache();
    void FindAllBuildCache();
    void BuildUpdatedCache();
    [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
}
