#include <SDL3/SDL.h>
#include <GLES3/gl3.h>

int BrowserProof_Version(void) { return SDL_GetVersion(); }
const char *BrowserProof_Error(void) { return SDL_GetError(); }

int BrowserProof_Init(void)
{
    if (!SDL_Init(SDL_INIT_VIDEO | SDL_INIT_AUDIO | SDL_INIT_EVENTS))
        return 1;
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_PROFILE_MASK, SDL_GL_CONTEXT_PROFILE_ES);
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_MAJOR_VERSION, 3);
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_MINOR_VERSION, 0);
    SDL_Window *window = SDL_CreateWindow("SDL3 static native proof", 320, 180, SDL_WINDOW_OPENGL);
    if (!window)
        return 2;
    SDL_GLContext context = SDL_GL_CreateContext(window);
    if (!context)
        return 3;
    GLint version = 0;
    glGetIntegerv(GL_MAJOR_VERSION, &version);
    glClearColor(0.1f, 0.4f, 0.2f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);
    SDL_GL_SwapWindow(window);
    SDL_GL_DestroyContext(context);
    SDL_DestroyWindow(window);
    SDL_Quit();
    return version == 3 ? 0 : 4;
}
