// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.
                
// This code is auto generated, don't modify it by hand.
// To regenerate it run: Tools/MonoGame.Generator.CTypes

#pragma once

#include "api_common.h"
#include "api_enums.h"
#include "api_structs.h"


struct MGP_Platform;
struct MGP_Window;
struct MGP_Cursor;

MG_EXPORT MGP_Platform* MGP_Platform_Create(MGGameRunBehavior& behavior);
MG_EXPORT void MGP_Platform_Destroy(MGP_Platform* platform);
MG_EXPORT void MGP_Platform_BeforeInitialize(MGP_Platform* platform);
MG_EXPORT mgbyte MGP_Platform_PollEvent(MGP_Platform* platform, MGP_Event& event_);
MG_EXPORT mgbyte MGP_Platform_SetLiveResizeCallback(MGP_Platform* platform, void* callback);
MG_EXPORT void* MGP_Platform_GetTextEvent(MGP_Platform* platform);
MG_EXPORT mgbyte MGP_Window_SupportsTextComposition(MGP_Window* window);
MG_EXPORT mgbyte MGP_Window_SetTextInputActive(MGP_Window* window, mgbyte active);
MG_EXPORT mgbyte MGP_Window_SetTextInputRectangle(MGP_Window* window, mgint x, mgint y, mgint width, mgint height);
MG_EXPORT void MGP_Platform_StartRunLoop(MGP_Platform* platform);
MG_EXPORT mgbyte MGP_Platform_BeforeRun(MGP_Platform* platform);
MG_EXPORT mgbyte MGP_Platform_BeforeUpdate(MGP_Platform* platform);
MG_EXPORT mgbyte MGP_Platform_BeforeDraw(MGP_Platform* platform);
MG_EXPORT void* MGP_Platform_MakePath(const char* location, const char* path);
MG_EXPORT void MGP_Platform_Free(void* ptr);
MG_EXPORT MGMonoGamePlatform MGP_Platform_GetPlatform();
MG_EXPORT MGGraphicsBackend MGP_Platform_GetGraphicsBackend();
MG_EXPORT mgint MGP_Platform_GetSdlVersion();
MG_EXPORT mgbyte MGP_Platform_PushSdlEvent(void* event_);
MG_EXPORT MGP_Window* MGP_Window_Create(MGP_Platform* platform, mgint& width, mgint& height, const char* title);
MG_EXPORT void MGP_Window_Destroy(MGP_Window* window);
MG_EXPORT void MGP_Window_SetIconBitmap(MGP_Window* window, mgbyte* icon, mgint length);
MG_EXPORT void* MGP_Window_GetNativeHandle(MGP_Window* window);
// The operating system's own window object - NSWindow* on macOS, HWND on Windows, the X11 window
// id on Linux - as opposed to MGP_Window_GetNativeHandle, which returns the SDL_Window*. Platform
// integrations that have to talk to the OS rather than to SDL need this one: an accessibility
// adapter, a native menu, an IME panel. Null when the backend has no real window, which is the
// normal answer under the headless runtime and the dummy video driver, so callers must handle it.
MG_EXPORT void* MGP_Window_GetPlatformHandle(MGP_Window* window);
MG_EXPORT mgulong MGP_Window_GetSdlFlags(MGP_Window* window);
MG_EXPORT mgbyte MGP_Window_GetAllowUserResizing(MGP_Window* window);
MG_EXPORT void MGP_Window_SetAllowUserResizing(MGP_Window* window, mgbyte allow);
MG_EXPORT mgbyte MGP_Window_GetIsBorderless(MGP_Window* window);
MG_EXPORT void MGP_Window_SetIsBorderless(MGP_Window* window, mgbyte borderless);
MG_EXPORT void MGP_Window_SetTitle(MGP_Window* window, const char* title);
MG_EXPORT void MGP_Window_Show(MGP_Window* window, mgbyte show);
MG_EXPORT void MGP_Window_Raise(MGP_Window* window);
MG_EXPORT void MGP_Window_GetPosition(MGP_Window* window, mgint& x, mgint& y);
MG_EXPORT void MGP_Window_GetDrawableSize(MGP_Window* window, mgint& width, mgint& height);
MG_EXPORT void MGP_Window_SetPosition(MGP_Window* window, mgint x, mgint y);
MG_EXPORT void MGP_Window_SetClientSize(MGP_Window* window, mgint width, mgint height);
MG_EXPORT void MGP_Window_SetCursor(MGP_Window* window, MGP_Cursor* cursor);
MG_EXPORT mgint MGP_Window_ShowMessageBox(MGP_Window* window, const char* title, const char* description, const char* buttons, mgint count);
MG_EXPORT void MGP_Window_EnterFullScreen(MGP_Window* window, mgbyte useHardwareModeSwitch);
MG_EXPORT void MGP_Window_ExitFullScreen(MGP_Window* window);
MG_EXPORT void MGP_Mouse_SetVisible(MGP_Platform* platform, mgbyte visible);
MG_EXPORT void MGP_Mouse_WarpPosition(MGP_Window* window, mgint x, mgint y);
MG_EXPORT MGP_Cursor* MGP_Cursor_Create(MGSystemCursor cursor);
MG_EXPORT MGP_Cursor* MGP_Cursor_CreateCustom(mgbyte* rgba, mgint width, mgint height, mgint originx, mgint originy);
MG_EXPORT void MGP_Cursor_Destroy(MGP_Cursor* cursor);
MG_EXPORT mgint MGP_GamePad_GetMaxSupported();
MG_EXPORT void MGP_GamePad_GetCaps(MGP_Platform* platform, mgint identifer, MGP_ControllerCaps* caps);
MG_EXPORT mgbyte MGP_GamePad_SetVibration(MGP_Platform* platform, mgint identifer, mgfloat leftMotor, mgfloat rightMotor, mgfloat leftTrigger, mgfloat rightTrigger);
