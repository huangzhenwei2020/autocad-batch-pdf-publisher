#include <windows.h>
#include <windowsx.h>
#include "D3D11/Device.h"
#include "D3D11/SwapChain.h"
#include "D3D11/D3D11Renderer.h"
#include "D3D11/D3D11RenderTarget.h"
#include "Document/DocumentManager.h"
#include "Document/Command/LayerCommands.h"
#include "Document/Command/LayoutCommands.h"
#include "Scene/HatchPatternLibrary.h"
#include "Editor/Tools/LeaderTool.h"
#include "Editor/Tools/MLeaderTool.h"
#include "Editor/Input/InputEvent.h"
#include "Text/FontSystem.h"
#include "Core/PathUtils.h"
#include <atomic>
#include <cctype>
#include <iomanip>
#include <limits>
#include <sstream>
#include <stdexcept>

using namespace MiniCAD;
namespace
{
    thread_local std::string lastError;
    constexpr UINT TextMessage = WM_APP + 610;
    constexpr UINT ShortcutMessage = WM_APP + 611;
    constexpr wchar_t WindowClass[] = L"Wanluo.NativeCadViewport.1";

    std::string Quote(const std::string& text)
    {
        std::ostringstream result;
        result << '"';
        for (unsigned char ch : text)
        {
            if (ch == '"' || ch == '\\') result << '\\' << ch;
            else if (ch < 32) result << "\\u" << std::hex << std::setw(4) << std::setfill('0') << int(ch) << std::dec;
            else result << ch;
        }
        result << '"';
        return result.str();
    }

    class NullRenderer final : public IRenderer
    {
        void BeginFrame(IRenderTarget&, const ViewportDesc&) override {}
        void EndFrame() override {}
        void Submit(std::span<const Vertex_P3_C4>, const Math::Mat4&, PrimitiveType, bool, bool) override {}
        void SubmitTextured(std::span<const Vertex_P3_C4_UV>, const Math::Mat4&, void*, bool, bool) override {}
        void* GetNativeDevice() override { return nullptr; }
    };

    class CadRenderer final : public D3D11Renderer
    {
        ID3D11DeviceContext* context;
    public:
        CadRenderer(ID3D11Device* device, ID3D11DeviceContext* context) : D3D11Renderer(device, context), context(context) {}
        void BeginFrame(IRenderTarget& target, const ViewportDesc& view) override
        {
            D3D11Renderer::BeginFrame(target, view);
            // Cached scene layers are copied into the canvas, so both use the same background.
            const float background[] = {0.129f, 0.157f, 0.184f, 1.0f};
            context->ClearRenderTargetView(static_cast<ID3D11RenderTargetView*>(target.GetNativeHandle()), background);
        }
    };

    struct LoadJob
    {
        std::string path;
        std::string error;
        std::atomic<float> progress{0};
        std::atomic<bool> cancelled{false};
        std::unique_ptr<Document> document;
    };

    struct Session
    {
        NullRenderer nullRenderer;
        FontSystem fonts;
        DocumentManager manager;
        std::unique_ptr<Device> device;
        std::unique_ptr<SwapChain> swap;
        std::unique_ptr<CadRenderer> renderer;
        std::unique_ptr<D3D11RenderTarget> target;
        HWND window = nullptr;
        bool busy = false;
        bool pointerInside = false;
        bool middlePan = false;
        bool releasingCapture = false;
        POINT previous{}, pressed{};
        std::string state;
        std::string renderError;
        std::vector<unsigned char> pixels;

        explicit Session(const char* fontDirectory)
        {
            fonts.Initialize();
            auto& engine = fonts.GetFontEngine();
            if (fontDirectory && *fontDirectory) engine.SetFontDir(std::string(fontDirectory) + "/");
            wchar_t windows[MAX_PATH]{};
            GetWindowsDirectoryW(windows, MAX_PATH);
            auto systemFonts = (std::filesystem::path(windows) / L"Fonts").u8string();
            engine.AddSearchDir(std::string(systemFonts.begin(), systemFonts.end()) + "/");
            manager.SetFontSystem(&fonts);
            manager.InitViewport(nullRenderer, 1000, 700);
        }

        ~Session() { Detach(); }
        Document& DocumentRef()
        {
            auto* doc = manager.GetActive();
            if (!doc) throw std::runtime_error("请新建或打开图纸。");
            return *doc;
        }
        Editor& EditorRef() { DocumentRef(); return manager.GetEditor(); }
        Document* Find(uint64_t id)
        {
            auto* doc = reinterpret_cast<Document*>(static_cast<uintptr_t>(id));
            if (!manager.Contains(doc)) throw std::runtime_error("图纸已关闭，请重新选择。");
            return doc;
        }
        void Invalidate() { if (window) InvalidateRect(window, nullptr, FALSE); }

        enum class PointerKind { Arrow, Idle, Point, Select, Pan, Move, Busy };

        PointerKind PointerMode()
        {
            if (busy) return PointerKind::Busy;
            if (!manager.GetActive()) return PointerKind::Arrow;
            auto& editor = EditorRef();
            if (middlePan || editor.HidesCrosshair()) return PointerKind::Pan;
            if (editor.GetGripEditor().IsDragging()) return PointerKind::Move;
            if (editor.IsBoxSelecting()) return PointerKind::Point;
            if (editor.IsSelectingForCommand()) return PointerKind::Select;
            if (editor.IsToolRunning()) return PointerKind::Point;
            return PointerKind::Idle;
        }

        HCURSOR SystemCursor()
        {
            switch (PointerMode())
            {
                case PointerKind::Arrow: return LoadCursorW(nullptr, IDC_ARROW);
                case PointerKind::Busy: return LoadCursorW(nullptr, IDC_WAIT);
                case PointerKind::Pan:
                case PointerKind::Move: return LoadCursorW(nullptr, IDC_SIZEALL);
                default: return nullptr; // The viewport renders the CAD crosshair/pickbox.
            }
        }

        void RefreshCursor()
        {
            if (!window) return;
            POINT point{}; GetCursorPos(&point);
            if (GetCapture() == window || WindowFromPoint(point) == window) SetCursor(SystemCursor());
        }

        void EnterPointer()
        {
            if (pointerInside) return;
            pointerInside = true;
            TRACKMOUSEEVENT track{ sizeof(track), TME_LEAVE, window, 0 };
            TrackMouseEvent(&track);
            Invalidate();
        }

        void EndPointerInteraction(bool cancelGrip)
        {
            middlePan = false;
            if (!busy && manager.GetActive())
            {
                auto& editor = EditorRef();
                editor.EndTemporaryNavigation();
                if (cancelGrip && editor.GetGripEditor().IsDragging())
                {
                    editor.GetGripEditor().CancelDrag();
                    editor.GetGripEditor().MarkDirty();
                }
            }
            if (window && GetCapture() == window)
            {
                releasingCapture = true;
                ReleaseCapture();
                releasingCapture = false;
            }
            RefreshCursor();
            Invalidate();
        }

        void Detach()
        {
            EndPointerInteraction(true);
            pointerInside = false;
            if (window) { DestroyWindow(window); window = nullptr; }
            manager.SetRenderer(&nullRenderer);
            target.reset(); renderer.reset(); swap.reset(); device.reset();
        }

        void Resize()
        {
            RECT rect{};
            GetClientRect(window, &rect);
            int width = std::max(1L, rect.right), height = std::max(1L, rect.bottom);
            manager.GetMainViewport().Resize(float(width), float(height));
            if (device)
            {
                device->GetContext()->OMSetRenderTargets(0, nullptr, nullptr);
                swap->Resize(width, height);
                target->Resize(width, height);
                if (!target->GetTexture()) throw std::runtime_error("无法调整 CAD 绘图区显存，请缩小窗口后重试。");
            }
            Invalidate();
        }

        void Render()
        {
            if (busy || !renderer || !window || !IsWindowVisible(window)) return;
            if (!manager.GetActive())
            {
                const float background[] = {0.129f, 0.157f, 0.184f, 1};
                device->GetContext()->ClearRenderTargetView(swap->GetRTV(), background);
            }
            else
            {
                auto& editor = manager.GetMainEditor();
                editor.EnsureSceneCached(*renderer);
                editor.Render();
                auto view = editor.BuildViewState();
                const auto pointer = PointerMode();
                view.ShowCrosshair = pointerInside && (pointer == PointerKind::Idle || pointer == PointerKind::Point || pointer == PointerKind::Select);
                view.ShowCurrorBox = pointer == PointerKind::Idle || pointer == PointerKind::Select;
                if (pointer == PointerKind::Select) view.CursorSize = 0;
                manager.GetMainViewport().Render(*renderer, *target, view);
                if (Viewport::SceneUploaded(*renderer, view)) editor.ReleaseUploadedSceneStreams();
                Microsoft::WRL::ComPtr<ID3D11Texture2D> buffer;
                ThrowIfFailed(swap->GetSwapChain()->GetBuffer(0, IID_PPV_ARGS(&buffer)));
                device->GetContext()->OMSetRenderTargets(0, nullptr, nullptr);
                device->GetContext()->CopyResource(buffer.Get(), target->GetTexture());
            }
            swap->Present();
        }

        static KeyCode Key(WPARAM key)
        {
            if (key >= 'A' && key <= 'Z') return static_cast<KeyCode>(int(KeyCode::A) + int(key - 'A'));
            if (key >= VK_F1 && key <= VK_F12) return static_cast<KeyCode>(int(KeyCode::F1) + int(key - VK_F1));
            switch (key)
            {
                case VK_ESCAPE: return KeyCode::Escape;
                case VK_RETURN: return KeyCode::Enter;
                case VK_SPACE: return KeyCode::Space;
                case VK_DELETE: return KeyCode::Delete;
                default: return KeyCode::Unknown;
            }
        }

        void Input(UINT message, WPARAM wparam, LPARAM lparam)
        {
            if (busy) return;
            InputEvent input{};
            input.MouseX = previous.x; input.MouseY = previous.y;
            input.Modifiers = (GetKeyState(VK_SHIFT) < 0 ? 1 : 0) | (GetKeyState(VK_CONTROL) < 0 ? 2 : 0) | (GetKeyState(VK_MENU) < 0 ? 4 : 0);
            if (message == WM_KEYDOWN && (((input.Modifiers & 2)
                && (wparam == 'S' || wparam == 'O' || wparam == 'N' || wparam == '0' || wparam == '1' || wparam == VK_F1 || wparam == VK_TAB)) || wparam == VK_F2))
            {
                SendMessageW(window, ShortcutMessage, wparam, (input.Modifiers & 1) ? 1 : 0);
                return;
            }
            if (!manager.GetActive()) return;
            auto& editor = EditorRef();
            if (message == WM_KEYDOWN)
            {
                if (wparam >= 'A' && wparam <= 'Z' && !(input.Modifiers & 2)) return;
                input.Type = InputEventType::KeyDown;
                input.Key = Key(wparam);
                if ((wparam == VK_RETURN || wparam == VK_SPACE) && !editor.IsToolRunning() && !editor.IsBoxSelecting()) editor.RunCommand("");
                else editor.OnInput(input);
                if (wparam == VK_ESCAPE && middlePan)
                {
                    EndPointerInteraction(false);
                    if (GetCapture() == window) ReleaseCapture();
                }
                RefreshCursor();
                Invalidate();
                return;
            }
            input.MouseX = GET_X_LPARAM(lparam); input.MouseY = GET_Y_LPARAM(lparam);
            if (message != WM_MOUSEWHEEL)
            {
                RECT client{}; GetClientRect(window, &client);
                if (PtInRect(&client, { input.MouseX, input.MouseY })) EnterPointer();
                else pointerInside = false;
            }
            input.LastMouseX = previous.x; input.LastMouseY = previous.y;
            input.PressMouseX = pressed.x; input.PressMouseY = pressed.y;
            input.MouseButtons = ((wparam & MK_LBUTTON) ? 1 : 0) | ((wparam & MK_MBUTTON) ? 2 : 0) | ((wparam & MK_RBUTTON) ? 4 : 0);
            if (message == WM_MOUSEWHEEL)
            {
                POINT point{input.MouseX, input.MouseY}; ScreenToClient(window, &point);
                input.MouseX = point.x; input.MouseY = point.y;
                input.Type = InputEventType::MouseWheel;
                input.WheelDelta = float(GET_WHEEL_DELTA_WPARAM(wparam)) / WHEEL_DELTA;
            }
            else if (message == WM_MOUSEMOVE) input.Type = InputEventType::MouseMove;
            else
            {
                const bool down = message == WM_LBUTTONDOWN || message == WM_MBUTTONDOWN || message == WM_RBUTTONDOWN || message == WM_MBUTTONDBLCLK;
                input.Type = down ? InputEventType::MouseButtonDown : InputEventType::MouseButtonUp;
                input.Button = message == WM_LBUTTONDOWN || message == WM_LBUTTONUP ? MouseButton::Left :
                    message == WM_RBUTTONDOWN || message == WM_RBUTTONUP ? MouseButton::Right : MouseButton::Middle;
                if (down) { SetFocus(window); SetCapture(window); pressed = {input.MouseX, input.MouseY}; input.PressMouseX = pressed.x; input.PressMouseY = pressed.y; }
                if (input.Button == MouseButton::Middle) middlePan = down && message != WM_MBUTTONDBLCLK;
                if (message == WM_MBUTTONDBLCLK) input.ClickCount = 2;
            }
            editor.OnInput(input);
            // Deliver the drop before capture-loss cleanup so committed grips survive.
            if (input.Type == InputEventType::MouseButtonUp && !input.MouseButtons && GetCapture() == window)
            {
                releasingCapture = true;
                ReleaseCapture();
                releasingCapture = false;
            }
            previous = {input.MouseX, input.MouseY};
            RefreshCursor();
            Invalidate();
        }

        static LRESULT CALLBACK Procedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
        {
            auto* session = reinterpret_cast<Session*>(GetWindowLongPtrW(window, GWLP_USERDATA));
            if (message == WM_NCCREATE)
            {
                session = static_cast<Session*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);
                SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(session));
                session->window = window;
            }
            if (!session) return DefWindowProcW(window, message, wparam, lparam);
            try
            {
                switch (message)
                {
                    case WM_SETCURSOR:
                        if (LOWORD(lparam) == HTCLIENT)
                        {
                            session->EnterPointer();
                            SetCursor(session->SystemCursor());
                            return TRUE;
                        }
                        break;
                    case WM_MOUSELEAVE:
                        session->pointerInside = false;
                        session->Invalidate();
                        return 0;
                    case WM_CAPTURECHANGED:
                        if (!session->releasingCapture) session->EndPointerInteraction(true);
                        return 0;
                    case WM_CANCELMODE:
                        session->EndPointerInteraction(true);
                        if (GetCapture() == window) ReleaseCapture();
                        return 0;
                    case WM_KILLFOCUS:
                        if (session->middlePan)
                        {
                            session->EndPointerInteraction(false);
                            if (GetCapture() == window) ReleaseCapture();
                        }
                        break;
                    case WM_SIZE: if (!session->busy) session->Resize(); return 0;
                    case WM_ERASEBKGND: return 1;
                    case WM_GETDLGCODE: return DLGC_WANTALLKEYS | DLGC_WANTCHARS;
                    case WM_PAINT:
                    {
                        PAINTSTRUCT paint{}; BeginPaint(window, &paint);
                        try { session->Render(); }
                        catch (...) { EndPaint(window, &paint); throw; }
                        EndPaint(window, &paint); return 0;
                    }
                    case WM_TIMER:
                        if (!session->busy && session->manager.GetActive() && (session->EditorRef().IsBuildingDisplay() || GetFocus() == window)) session->Invalidate();
                        return 0;
                    case WM_CHAR:
                        if (wparam > 32 && !(GetKeyState(VK_CONTROL) < 0)) SendMessageW(window, TextMessage, wparam, 0);
                        return 0;
                    case WM_KEYDOWN: case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_MBUTTONDOWN: case WM_MBUTTONUP:
                    case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_MOUSEMOVE: case WM_MOUSEWHEEL: case WM_MBUTTONDBLCLK:
                        session->Input(message, wparam, lparam); return 0;
                    case WM_DESTROY: KillTimer(window, 1); session->window = nullptr; return 0;
                }
            }
            catch (const std::exception& error) { session->renderError = error.what(); ValidateRect(window, nullptr); }
            return DefWindowProcW(window, message, wparam, lparam);
        }

        void Attach(HWND parent)
        {
            if (window) return;
            WNDCLASSEXW type{sizeof(type)};
            type.style = CS_DBLCLKS; type.lpfnWndProc = Procedure;
            type.hInstance = GetModuleHandleW(nullptr); type.hCursor = LoadCursorW(nullptr, IDC_ARROW); type.lpszClassName = WindowClass;
            if (!RegisterClassExW(&type) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) throw std::runtime_error("无法创建 CAD 绘图区。");
            if (!CreateWindowExW(0, WindowClass, L"", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0, 0, 1000, 700, parent, nullptr, type.hInstance, this)) throw std::runtime_error("无法创建 CAD 子窗口。");
            device = std::make_unique<Device>();
            try { device->Initialize(); } catch (...) { device = std::make_unique<Device>(); device->Initialize(true); }
            swap = std::make_unique<SwapChain>();
            SwapChain::Options options; options.backBufferFormat = DXGI_FORMAT_R8G8B8A8_UNORM; options.enableVSync = false;
            swap->Initialize(device.get(), window, 1000, 700, options);
            renderer = std::make_unique<CadRenderer>(device->GetDevice(), device->GetContext());
            target = std::make_unique<D3D11RenderTarget>(device->GetDevice()); target->Create(1000, 700);
            if (!target->GetTexture()) throw std::runtime_error("无法创建 CAD 绘图区显存，请关闭其他图形程序后重试。");
            manager.SetRenderer(renderer.get());
            SetTimer(window, 1, 25, nullptr);
            Resize();
        }

        void Command(const std::string& text)
        {
            auto& editor = EditorRef();
            if (text == "@undo" || text == "@redo")
            {
                if (text == "@undo") manager.Undo(); else manager.Redo();
                auto camera = manager.GetMainViewport().GetCamera().GetState();
                if (DocumentRef().ValidateActiveLayout(camera)) manager.GetMainViewport().GetCamera().SetState(camera);
            }
            else if (text == "@all") editor.SelectAll();
            else if (text == "@delete") editor.DeleteSelected();
            else if (text == "@fit" || _stricmp(text.c_str(), "ZF") == 0) editor.ZoomAll(false);
            else if (text == "@snap") editor.ToggleSnap();
            else if (text == "@ortho") editor.ToggleOrtho();
            else if (text == "@polar") editor.TogglePolar();
            else if (text == "@cancel")
            {
                InputEvent event{}; event.Type = InputEventType::KeyDown; event.Key = KeyCode::Escape;
                event.MouseX = previous.x; event.MouseY = previous.y;
                editor.OnInput(event);
                EndPointerInteraction(false);
                if (GetCapture() == window) ReleaseCapture();
            }
            else if (text.empty() && (editor.IsToolRunning() || editor.IsBoxSelecting()))
            {
                InputEvent event{}; event.Type = InputEventType::KeyDown; event.Key = KeyCode::Enter;
                event.MouseX = previous.x; event.MouseY = previous.y;
                editor.OnInput(event);
            }
            else if (!editor.IsToolRunning() || !editor.SubmitCoordinateText(text))
            {
                if (editor.IsToolRunning() && text.size() == 1 && std::isalpha(static_cast<unsigned char>(text.front())))
                {
                    InputEvent event{}; event.Type = InputEventType::KeyDown;
                    event.Key = Key(static_cast<WPARAM>(std::toupper(static_cast<unsigned char>(text.front()))));
                    event.MouseX = previous.x; event.MouseY = previous.y;
                    editor.OnInput(event);
                }
                else editor.RunCommand(text);
            }
            Invalidate();
            RefreshCursor();
        }

        const char* State()
        {
            std::ostringstream json; json.imbue(std::locale::classic()); json << std::boolalpha << std::setprecision(15);
            auto* doc = manager.GetActive();
            static constexpr const char* pointerNames[] = { "arrow", "idle", "point", "select", "pan", "move", "busy" };
            json << "{\"activeId\":" << reinterpret_cast<uintptr_t>(doc) << ",\"cursorMode\":" << Quote(pointerNames[static_cast<size_t>(PointerMode())])
                << ",\"pointerInside\":" << pointerInside << ",\"error\":" << Quote(renderError) << ",\"documents\":[";
            bool comma = false;
            for (const auto& item : manager.GetAll())
            {
                if (comma) json << ','; comma = true;
                json << "{\"id\":" << reinterpret_cast<uintptr_t>(item.get()) << ",\"name\":" << Quote(item->GetName())
                    << ",\"path\":" << Quote(item->GetPath()) << ",\"dirty\":" << item->IsDirty() << '}';
            }
            json << ']';
            if (doc)
            {
                auto& editor = EditorRef();
                json << ",\"entityCount\":" << doc->GetScene().GetAllIDs().size() << ",\"selectionCount\":" << editor.GetSelection().size()
                    << ",\"canUndo\":" << doc->CanUndo() << ",\"canRedo\":" << doc->CanRedo() << ",\"snap\":" << editor.IsSnapEnabled()
                    << ",\"ortho\":" << editor.IsOrthoEnabled() << ",\"polar\":" << editor.IsPolarEnabled()
                    << ",\"prompt\":" << Quote(editor.IsBoxSelecting() ? "指定对角点，单击完成／Esc 取消" : editor.IsSelectingForCommand() ? "选择对象，空格或回车确认" : editor.IsToolRunning() ? editor.GetCmdLine().Prompt() : "命令") << ",\"buildProgress\":" << editor.DisplayBuildProgress()
                    << ",\"saveNotice\":" << Quote(doc->GetCadSaveVersion() == CadSaveVersion::R2007 ? "原图采用 2007 格式，DWG 将写为 2010 格式。" : "");
                json << ",\"log\":["; comma = false;
                const auto& lines = editor.GetCmdLine().Lines();
                for (size_t index = lines.size() > 10 ? lines.size() - 10 : 0; index < lines.size(); ++index)
                { if (comma) json << ','; comma = true; json << Quote(lines[index]); }
                json << "],\"layers\":["; comma = false;
                auto& layers = doc->GetLayerManager();
                for (auto id : layers.GetAllLayerIDs())
                {
                    auto* layer = layers.GetLayer(id); if (!layer) continue;
                    if (comma) json << ','; comma = true;
                    json << "{\"id\":" << id << ",\"name\":" << Quote(layer->GetName()) << ",\"visible\":" << layer->IsVisible()
                        << ",\"locked\":" << layer->IsLocked() << ",\"current\":" << (id == layers.GetActiveLayerID())
                        << ",\"rgb\":" << ((int(std::round(layer->GetColor().r * 255)) << 16) | (int(std::round(layer->GetColor().g * 255)) << 8) | int(std::round(layer->GetColor().b * 255)))
                        << ",\"weight\":" << int(layer->GetLineweight()) << '}';
                }
                json << "],\"layouts\":["; comma = false;
                const auto& layouts = doc->GetScene().Layouts();
                for (size_t index = 0; index < layouts.size(); ++index)
                {
                    if (comma) json << ','; comma = true;
                    json << "{\"id\":" << index << ",\"name\":" << Quote(layouts[index].Name) << ",\"model\":" << layouts[index].Model << ",\"current\":" << (int(index) == doc->ActiveLayout()) << '}';
                }
                json << "],\"properties\":["; comma = false;
                for (const auto& property : editor.GetSelectionProperties())
                {
                    if (comma) json << ','; comma = true;
                    json << "{\"name\":" << Quote(property.name) << ",\"kind\":" << int(property.kind) << ",\"editable\":" << property.editable << ",\"value\":";
                    if (!property.value) json << "null";
                    else if (auto number = std::get_if<double>(&*property.value)) json << Quote(std::to_string(*number));
                    else json << Quote(std::get<std::string>(*property.value));
                    json << '}';
                }
                const auto point = manager.GetMainViewport().GetCamera().ScreenToWorld(double(previous.x), double(previous.y));
                json << "],\"x\":" << point.x << ",\"y\":" << point.y;
            }
            json << '}'; state = json.str(); return state.c_str();
        }
    };

    template<class Action> int Protect(Action action)
    {
        try { lastError.clear(); action(); return 1; }
        catch (const std::exception& error) { lastError = error.what(); return 0; }
        catch (...) { lastError = "CAD 内核操作未完成。"; return 0; }
    }
}

#define API extern "C" __declspec(dllexport)
API const char* wl_error() { return lastError.c_str(); }
API void* wl_create(const char* fonts)
{
    Session* session = nullptr; Protect([&] { session = new Session(fonts); }); return session;
}
API void wl_destroy(Session* session) { delete session; }
API int wl_attach(Session* session, HWND parent) { return Protect([&] { session->Attach(parent); }); }
API HWND wl_window(Session* session) { return session->window; }
API void wl_detach(Session* session) { session->Detach(); }
API int wl_new(Session* session) { return Protect([&]
{
    session->EndPointerInteraction(true);
    session->manager.New();
    session->EditorRef().RegisterAlias("A", "Arc");
    session->EditorRef().SetHatchDialogEnabled(true);
    auto& doc = session->DocumentRef(); doc.SetCadSaveVersion(CadSaveVersion::R2018);
    auto camera = session->manager.GetMainViewport().GetCamera().GetState();
    camera.Zoom = 10000;
    session->manager.GetMainViewport().GetCamera().SetState(camera);
    if (auto* standard = doc.GetScene().GetTextStyleTable().Find(0)) { standard->FontFile = "arial.ttf"; standard->BigFontFile.clear(); }
    doc.MarkSaved();
    session->Invalidate();
    session->RefreshCursor();
}); }
API int wl_command(Session* session, const char* command) { return Protect([&] { session->Command(command ? command : ""); }); }
API const char* wl_state(Session* session)
{
    const char* state = nullptr; Protect([&] { state = session->State(); }); return state;
}
API int wl_activate(Session* session, uint64_t id) { return Protect([&] { session->EndPointerInteraction(true); session->manager.SetActive(session->Find(id)); session->Invalidate(); session->RefreshCursor(); }); }
API int wl_close(Session* session, uint64_t id) { return Protect([&] { session->EndPointerInteraction(true); session->manager.Close(session->Find(id)); session->Invalidate(); session->RefreshCursor(); }); }
API int wl_layout(Session* session, int index) { return Protect([&]
{
    CameraState camera;
    if (session->DocumentRef().SetActiveLayout(index, camera)) session->manager.GetMainViewport().GetCamera().SetState(camera);
    session->Invalidate();
}); }
API int wl_layer(Session* session, uint32_t id, int visible, int locked, int current) { return Protect([&]
{
    auto& doc = session->DocumentRef();
    LayerChange change; change.Visible = visible != 0; change.Locked = locked != 0;
    doc.GetCommandStack().Execute(std::make_unique<ChangeLayerCommand>(id, change), doc.GetScene());
    if (current) doc.GetLayerManager().SetActiveLayerID(id);
    session->Invalidate();
}); }
API int wl_property(Session* session, const char* name, const char* value, int kind) { return Protect([&]
{
    PropValue property = std::string(value ? value : "");
    if (kind != int(PropKind::String))
    {
        size_t consumed = 0; const auto number = std::stod(value, &consumed);
        if (consumed != std::strlen(value) || !std::isfinite(number)) throw std::invalid_argument("请输入有效数值。");
        property = number;
    }
    if (!session->EditorRef().SetSelectionProperty(name, property)) throw std::invalid_argument("参数未应用，请检查数值或选择对象。");
    session->Invalidate();
}); }
API int wl_point(Session* session, double x, double y) { return Protect([&]
{
    if (!std::isfinite(x) || !std::isfinite(y) || !session->EditorRef().SubmitPoint({x, y, 0})) throw std::invalid_argument("当前工具无法接收坐标。");
    session->Invalidate();
}); }
API int wl_world_to_screen(Session* session, double x, double y, int* pixelX, int* pixelY) { return Protect([&]
{
    const auto point = session->manager.GetMainViewport().GetCamera().WorldToScreen({x, y, 0});
    *pixelX = int(std::round(point.x)); *pixelY = int(std::round(point.y));
}); }
API int wl_busy(Session* session, int busy) { return Protect([&]
{
    if (busy) session->EndPointerInteraction(false);
    session->busy = busy != 0;
    if (!busy) { if (session->window) session->Resize(); session->Invalidate(); }
    session->RefreshCursor();
}); }
API int wl_stage_save(Session* session, const char* path) { return Protect([&]
{
    if (!session->DocumentRef().SaveToFile(path)) throw std::runtime_error("图纸文件写入未完成，请检查路径和写入权限。");
}); }
API int wl_saved(Session* session, const char* path) { return Protect([&]
{
    auto& doc = session->DocumentRef();
    if (CadKindFromPath(path) == CadFileKind::Dwg && doc.GetCadSaveVersion() == CadSaveVersion::R2007) doc.SetCadSaveVersion(CadSaveVersion::R2010);
    doc.SetPath(path); doc.MarkSaved();
}); }
API LoadJob* wl_job_create(const char* path)
{
    LoadJob* result = nullptr;
    Protect([&] { auto job = std::make_unique<LoadJob>(); job->path = path; result = job.release(); });
    return result;
}
API int wl_job_run(LoadJob* job)
{
    try
    {
        auto doc = std::make_unique<Document>();
        if (!doc->LoadFromFile(job->path, &job->error, [job](float progress)
            { job->progress = progress; if (job->cancelled) throw std::runtime_error("已取消打开图纸。"); })) return 0;
        if (job->cancelled) return 0;
        job->document = std::move(doc); job->progress = 1; return 1;
    }
    catch (const std::exception& error) { job->error = error.what(); return 0; }
}
API const char* wl_job_error(LoadJob* job) { return job->error.c_str(); }
API float wl_job_progress(LoadJob* job) { return job->progress; }
API void wl_job_cancel(LoadJob* job) { job->cancelled = true; }
API void wl_job_destroy(LoadJob* job) { delete job; }
API int wl_job_accept(Session* session, LoadJob* job) { return Protect([&]
{
    if (!job->document || job->cancelled) throw std::runtime_error("图纸读取已取消。");
    session->EndPointerInteraction(true);
    auto* existing = session->manager.FindOpen(job->path);
    if (existing) session->manager.SetActive(existing);
    else
    {
        job->document->SetFontSystem(&session->fonts);
        session->manager.Add(std::move(job->document));
        session->EditorRef().BeginProgressiveBuild(20);
    }
    session->EditorRef().RegisterAlias("A", "Arc");
    session->EditorRef().SetHatchDialogEnabled(true);
    session->Invalidate();
    session->RefreshCursor();
}); }
API int wl_pixels(Session* session, const unsigned char** bytes, int* width, int* height) { return Protect([&]
{
    session->Render();
    if (!session->target) throw std::runtime_error("绘图区尚未就绪。");
    auto* texture = session->target->GetTexture();
    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> copy;
    ThrowIfFailed(session->device->GetDevice()->CreateTexture2D(&desc, nullptr, &copy));
    auto* context = session->device->GetContext(); context->CopyResource(copy.Get(), texture);
    D3D11_MAPPED_SUBRESOURCE mapped{}; ThrowIfFailed(context->Map(copy.Get(), 0, D3D11_MAP_READ, 0, &mapped));
    session->pixels.resize(size_t(desc.Width) * desc.Height * 4);
    for (UINT y = 0; y < desc.Height; ++y)
    {
        auto* from = static_cast<const unsigned char*>(mapped.pData) + y * mapped.RowPitch;
        auto* to = session->pixels.data() + size_t(y) * desc.Width * 4;
        for (UINT x = 0; x < desc.Width; ++x) { to[x*4] = from[x*4+2]; to[x*4+1] = from[x*4+1]; to[x*4+2] = from[x*4]; to[x*4+3] = 255; }
    }
    context->Unmap(copy.Get(), 0); *width = int(desc.Width); *height = int(desc.Height); *bytes = session->pixels.data();
}); }

namespace
{
    void RequireActive(Session* session, uint64_t document)
    {
        if (session->busy || !session->manager.GetActive() || reinterpret_cast<uintptr_t>(session->manager.GetActive()) != document)
            throw std::runtime_error("图纸状态已变化，请重新执行操作。");
    }
    void Finite(double value)
    {
        if (!std::isfinite(value)) throw std::invalid_argument("请输入有效数值。");
    }
    void Positive(double value)
    {
        Finite(value);
        if (value <= 0) throw std::invalid_argument("参数必须大于0。");
    }
}

API const char* wl_dialog_state(Session* session)
{
    static thread_local std::string result;
    Protect([&]
    {
        std::ostringstream json; json.imbue(std::locale::classic()); json << std::boolalpha << std::setprecision(15);
        auto* doc = session->manager.GetActive();
        json << "{\"documentId\":" << reinterpret_cast<uintptr_t>(doc);
        if (doc && !session->busy)
        {
            auto& editor = session->EditorRef();
            json << ",\"radius\":" << editor.GetFilletRadius() << ",\"firstDistance\":" << editor.GetChamferFirst()
                << ",\"secondDistance\":" << editor.GetChamferSecond() << ",\"textHeight\":" << editor.CurrentTextHeight()
                << ",\"mlineScale\":" << editor.GetMLineSettings().Scale << ",\"mlineJustify\":" << int(editor.GetMLineSettings().Justify);
            const auto& dim = editor.GetDimensionStyle();
            json << ",\"dimHeight\":" << dim.TextHeight << ",\"dimArrow\":" << dim.ArrowSize << ",\"dimGap\":" << dim.TextGap
                << ",\"dimExtension\":" << dim.ExtLineExtend << ",\"dimOffset\":" << dim.ExtLineOffset
                << ",\"dimPrecision\":" << dim.Precision << ",\"dimTerminator\":" << int(dim.Terminator);
            auto& text = editor.GetTextInputRequest();
            auto& mtext = editor.GetMTextInputRequest();
            auto& array = editor.GetArrayRequest();
            auto& block = editor.GetBlockNameRequest();
            if (text.Active)
                json << ",\"kind\":\"text\",\"text\":" << Quote(text.InitialText) << ",\"height\":" << text.Height
                    << ",\"rotation\":" << text.Rotation * 180.0 / 3.141592653589793 << ",\"editing\":" << (text.EditTargetId != Object::InvalidID);
            else if (mtext.Active)
                json << ",\"kind\":\"mtext\",\"text\":" << Quote(mtext.DisplayText) << ",\"height\":" << mtext.Height
                    << ",\"rotation\":" << mtext.Rotation * 180.0 / 3.141592653589793 << ",\"width\":" << mtext.BoxWidth
                    << ",\"editing\":" << (mtext.EditTargetId != Object::InvalidID);
            else if (editor.GetHatchRequest().Active)
            {
                const auto& hatch = editor.GetHatchSettings();
                json << ",\"kind\":\"hatch\",\"pattern\":" << Quote(hatch.Pattern) << ",\"scale\":" << hatch.Scale << ",\"rotation\":" << hatch.AngleDeg << ",\"choices\":[";
                bool comma = false;
                for (const auto& pattern : HatchPatternLibrary::Instance().All())
                { if (comma) json << ','; comma = true; json << Quote(pattern.Name); }
                json << ']';
            }
            else if (array.Active && !array.PickingCenter)
            {
                const auto& p = array.Params;
                json << ",\"kind\":\"array\",\"rows\":" << p.rows << ",\"columns\":" << p.cols
                    << ",\"rowSpacing\":" << p.rowSpacing << ",\"columnSpacing\":" << p.colSpacing
                    << ",\"rotation\":" << p.angleDeg << ",\"centerX\":" << p.center.x << ",\"centerY\":" << p.center.y
                    << ",\"count\":" << p.count << ",\"fillAngle\":" << p.fillAngleDeg;
            }
            else if (block.Active) json << ",\"kind\":\"blockDefine\",\"text\":" << Quote(block.DefaultName);
            else if (editor.GetBlockInsertRequest().Active)
            {
                json << ",\"kind\":\"blockInsert\",\"blocks\":[";
                bool comma = false;
                doc->GetScene().GetBlockTable().ForEach([&](BlockID id, const BlockEntity& item)
                {
                    if (id <= BlockTable::PaperSpaceID || item.GetName().empty() || item.GetName()[0] == '*' || (item.GetFlags() & BlockEntity::XRef)) return;
                    if (comma) json << ','; comma = true;
                    json << "{\"id\":" << id << ",\"name\":" << Quote(item.GetName()) << '}';
                });
                json << ']';
            }
            else if (editor.GetImageRequest().Active) json << ",\"kind\":\"image\"";
        }
        json << '}'; result = json.str();
    });
    return result.c_str();
}

API int wl_dialog_cancel(Session* session, uint64_t document, const char* kind) { return Protect([&]
{
    RequireActive(session, document);
    auto& editor = session->EditorRef();
    const std::string name = kind ? kind : "";
    if (name == "text") editor.GetTextInputRequest() = {};
    else if (name == "mtext") editor.GetMTextInputRequest() = {};
    else if (name == "hatch") editor.CancelHatch();
    else if (name == "array") editor.CancelArray();
    else if (name == "blockDefine") editor.GetBlockNameRequest() = {};
    else if (name == "blockInsert") editor.GetBlockInsertRequest() = {};
    else if (name == "image") editor.GetImageRequest() = {};
    session->Invalidate(); session->RefreshCursor();
}); }

API int wl_text_submit(Session* session, uint64_t document, int multiline, const char* text, double height, double rotation, double width) { return Protect([&]
{
    RequireActive(session, document); Positive(height); Finite(rotation); Finite(width);
    if (width < 0) throw std::invalid_argument("文字宽度不能小于0。");
    auto& editor = session->EditorRef();
    const double radians = rotation * 3.141592653589793 / 180.0;
    if (multiline)
    {
        auto& request = editor.GetMTextInputRequest();
        if (!request.Active) throw std::runtime_error("请先指定多行文字的位置。");
        request.Height = height; request.Rotation = radians; request.BoxWidth = width;
        editor.SubmitMTextInput(text ? text : "");
    }
    else
    {
        auto& request = editor.GetTextInputRequest();
        if (!request.Active) throw std::runtime_error("请先指定文字的位置。");
        request.Height = static_cast<float>(height); request.Rotation = static_cast<float>(radians);
        editor.SubmitTextInput(text ? text : "");
    }
    editor.SetTextHeight(height);
    session->Invalidate();
}); }

API int wl_hatch_submit(Session* session, uint64_t document, const char* pattern, double scale, double angle) { return Protect([&]
{
    RequireActive(session, document); Positive(scale); Finite(angle);
    if (!pattern || !HatchPatternLibrary::Instance().Find(pattern)) throw std::invalid_argument("请选择有效填充图案。");
    auto& editor = session->EditorRef();
    if (!editor.GetHatchRequest().Active) throw std::runtime_error("请重新执行填充命令。");
    editor.GetHatchSettings() = {pattern, scale, angle}; editor.SubmitHatch();
    session->Invalidate(); session->RefreshCursor();
}); }

struct CadArrayParameters
{
    int type, rows, columns, count, rotateItems;
    double rowSpacing, columnSpacing, rotation, centerX, centerY, fillAngle;
};
API int wl_array_submit(Session* session, uint64_t document, const CadArrayParameters* values) { return Protect([&]
{
    RequireActive(session, document);
    if (!values) throw std::invalid_argument("缺少阵列参数。");
    const auto& p = *values;
    Finite(p.rowSpacing); Finite(p.columnSpacing); Finite(p.rotation); Finite(p.centerX); Finite(p.centerY); Finite(p.fillAngle);
    if (p.type < 0 || p.type > 1 || (p.type == 0 && (p.rows < 1 || p.columns < 1)) || (p.type == 1 && p.count < 2))
        throw std::invalid_argument("请检查阵列数量。");
    auto& editor = session->EditorRef(); auto& request = editor.GetArrayRequest();
    if (!request.Active) throw std::runtime_error("请先选择对象并执行阵列。");
    const auto copies = p.type == 0 ? int64_t(p.rows) * p.columns : p.count;
    if (copies < 2 || copies > std::numeric_limits<int>::max()) throw std::invalid_argument("阵列数量超出内核整数范围。");
    if (p.type == 0 && ((p.rows > 1 && p.rowSpacing == 0) || (p.columns > 1 && p.columnSpacing == 0))) throw std::invalid_argument("阵列间距不能为0。");
    if (p.type == 1 && (p.fillAngle == 0 || std::abs(p.fillAngle) > 360)) throw std::invalid_argument("环形填充角应在-360到360度之间且不为0。");
    auto& target = request.Params;
    target.type = p.type == 0 ? ArrayType::Rectangular : ArrayType::Polar;
    target.rows = p.rows; target.cols = p.columns; target.count = p.count; target.rotateItems = p.rotateItems != 0;
    target.rowSpacing = p.rowSpacing; target.colSpacing = p.columnSpacing; target.angleDeg = p.rotation;
    target.center = {p.centerX, p.centerY, 0}; target.fillAngleDeg = p.fillAngle;
    editor.SubmitArray(); session->Invalidate();
}); }

API int wl_block_submit(Session* session, uint64_t document, int define, const char* name, uint32_t blockId) { return Protect([&]
{
    RequireActive(session, document); auto& editor = session->EditorRef();
    if (define)
    {
        if (!editor.GetBlockNameRequest().Active) throw std::runtime_error("请先选择对象并指定块基点。");
        if (!name || !*name || std::strpbrk(name, "<>/\\\":;?*|,=`")) throw std::invalid_argument("请输入有效块名。");
        if (session->DocumentRef().GetScene().GetBlockTable().FindByName(name) != BlockTable::InvalidID) throw std::invalid_argument("块名已存在，请使用其他名称。");
        editor.SubmitBlockDefine(name);
    }
    else
    {
        if (!editor.GetBlockInsertRequest().Active || blockId <= BlockTable::PaperSpaceID || !session->DocumentRef().GetScene().GetBlockTable().Find(blockId)) throw std::invalid_argument("请选择有效的块。");
        editor.SubmitBlockInsert(blockId);
    }
    session->Invalidate(); session->RefreshCursor();
}); }

API int wl_image_submit(Session* session, uint64_t document, const char* path) { return Protect([&]
{
    RequireActive(session, document); auto& editor = session->EditorRef();
    if (!editor.GetImageRequest().Active) throw std::runtime_error("请重新执行图像命令。");
    if (!path || !editor.SubmitImagePath(path)) throw std::runtime_error("无法读取这张图像，请选择受支持的图片。");
    session->Invalidate(); session->RefreshCursor();
}); }

API int wl_corner_settings(Session* session, double radius, double first, double second) { return Protect([&]
{
    Finite(radius); Finite(first); Finite(second);
    if (radius < 0 || first < 0 || second < 0) throw std::invalid_argument("圆角半径和倒角距离不能小于0。");
    session->EditorRef().SetCornerParameters(radius, first, second);
}); }
API int wl_text_height(Session* session, double height) { return Protect([&] { Positive(height); session->EditorRef().SetTextHeight(height); }); }

API int wl_layer_manage(Session* session, int action, uint32_t id, const char* name, int rgb, int weight) { return Protect([&]
{
    auto& doc = session->DocumentRef(); auto& scene = doc.GetScene(); auto& layers = scene.GetLayerManager();
    if (action == 0 || action == 2)
    {
        if (!name || !*name) throw std::invalid_argument("请输入图层名称。");
        const auto* current = layers.GetLayer(id);
        if (action == 0 || !current || current->GetName() != name)
            if (std::strpbrk(name, "<>/\\\":;?*|,=`")) throw std::invalid_argument("图层名称包含CAD不支持的字符。");
        for (auto layerId : layers.GetAllLayerIDs())
            if ((action == 0 || layerId != id) && _stricmp(layers.GetLayer(layerId)->GetName().c_str(), name) == 0)
                throw std::invalid_argument("图层名称已存在。");
        if (action == 2 && id == 0 && std::strcmp(name, "0") != 0) throw std::invalid_argument("默认0图层需要保留原名称。");
    }
    std::unique_ptr<ICommand> command;
    if (action == 0)
    {
        if (!name || !*name || LayerOps::NameTaken(layers, name)) throw std::invalid_argument("图层名称为空或已存在。");
        command = std::make_unique<AddLayerCommand>(name);
    }
    else if (action == 1)
    {
        if (id == 0) throw std::invalid_argument("默认0图层需要保留。");
        command = std::make_unique<DeleteLayerCommand>(id);
    }
    else if (action == 2)
    {
        if (!name || !*name || LayerOps::NameTaken(layers, name, id)) throw std::invalid_argument("图层名称为空或已存在。");
        LayerChange change; change.Name = name;
        change.Color = Math::Color4{float((rgb >> 16) & 255) / 255, float((rgb >> 8) & 255) / 255, float(rgb & 255) / 255, 1};
        change.Lineweight = static_cast<Lineweight>(weight);
        command = std::make_unique<ChangeLayerCommand>(id, change);
    }
    else throw std::invalid_argument("未知图层操作。");
    if (!doc.GetCommandStack().Execute(std::move(command), scene) && action != 2) throw std::runtime_error("图层操作未完成。");
    session->Invalidate();
}); }

API int wl_layout_manage(Session* session, int action, int index, const char* name) { return Protect([&]
{
    auto& doc = session->DocumentRef(); auto& scene = doc.GetScene();
    std::unique_ptr<ICommand> command;
    if (action == 0)
    {
        if (!name || !*name) throw std::invalid_argument("请输入布局名称。");
        for (const auto& layout : scene.Layouts()) if (_stricmp(layout.Name.c_str(), name) == 0) throw std::invalid_argument("布局名称已存在。");
        DrawingLayout layout; layout.Name = name; layout.Model = false;
        command = std::make_unique<AddLayoutCommand>(layout, int(scene.Layouts().size()));
    }
    else if (action == 1)
    {
        if (index < 0 || index >= int(scene.Layouts().size()) || scene.Layouts()[index].Model) throw std::invalid_argument("请选择要删除的图纸布局。");
        if (RemoveLayoutCommand::PaperCount(scene) <= 1) throw std::invalid_argument("请至少保留一个图纸布局。");
        command = std::make_unique<RemoveLayoutCommand>(scene.Layouts()[index].OwnerKey);
    }
    else throw std::invalid_argument("未知布局操作。");
    if (!doc.GetCommandStack().Execute(std::move(command), scene)) throw std::runtime_error("布局操作未完成。");
    auto camera = session->manager.GetMainViewport().GetCamera().GetState();
    if (action == 0) doc.SetActiveLayout(int(scene.Layouts().size()) - 1, camera);
    else doc.ValidateActiveLayout(camera);
    session->manager.GetMainViewport().GetCamera().SetState(camera);
    session->Invalidate();
}); }
API int wl_leader_start(Session* session, int multiple, const char* text, double height) { return Protect([&]
{
    Positive(height); auto& editor = session->EditorRef();
    const std::string annotation = text ? text : "";
    if (multiple)
        editor.RegisterTool("MLeader", [annotation, height] { auto tool = std::make_unique<MLeaderTool>(); tool->SetDefaultText(annotation); tool->SetDefaultTextHeight(height); return tool; });
    else
        editor.RegisterTool("Leader", [annotation, height] { auto tool = std::make_unique<LeaderTool>(); tool->SetDefaultText(annotation); tool->SetDefaultTextHeight(height); return tool; });
    editor.SetTextHeight(height); editor.RunCommand(multiple ? "MLeader" : "Leader");
    session->Invalidate(); session->RefreshCursor();
}); }
API int wl_mline_settings(Session* session, double scale, int justify) { return Protect([&]
{
    Positive(scale);
    if (justify < 0 || justify > 2) throw std::invalid_argument("请选择有效的多线对正方式。");
    auto& settings = session->EditorRef().GetMLineSettings();
    settings.Scale = scale; settings.Justify = static_cast<MLineJustify>(justify);
}); }
API int wl_dimension_settings(Session* session, double height, double arrow, double gap, double extension, double offset, int precision, int terminator) { return Protect([&]
{
    Positive(height); Positive(arrow); Finite(gap); Finite(extension); Finite(offset);
    if (gap < 0 || extension < 0 || offset < 0 || precision < 0 || precision > 15 || terminator < 0 || terminator > 1)
        throw std::invalid_argument("请检查标注间距、精度及端点形式。");
    auto& style = session->EditorRef().GetDimensionStyle();
    style.TextHeight = height; style.ArrowSize = arrow; style.TextGap = gap; style.ExtLineExtend = extension;
    style.ExtLineOffset = offset; style.Precision = precision; style.Terminator = static_cast<DimTerminator>(terminator);
}); }
