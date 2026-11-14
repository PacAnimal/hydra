// game-probe: a stand-in game for the desktop lanes. A fullscreen window takes the pointer in relative mode
// (pointer lock + relative motion) and prints, one JSON line each, flushed:
//   {"ready":true,"relative":true}          once focused and relative mode holds
//   {"ready":false,"focus":b,"relative":b}  if that never happens by the deadline (exits 2)
//   {"rel":[xrel,yrel],"pos":[x,y]}         per mouse motion event
//   {"button":n,"pressed":b}                per button press or release
// Exits on stdin EOF or SDL_EVENT_QUIT.
#include <SDL3/SDL.h>
#include <stdbool.h>
#include <stdio.h>

#define READY_DEADLINE_MS 20000
#define RETRY_MS 100

static Uint32 stdin_closed_event;

// stdin EOF ends the probe: the lane closes our stdin to stop us
static int watch_stdin(void *unused)
{
    (void)unused;
    char buf[256];
    while (fread(buf, 1, sizeof buf, stdin) > 0)
        ;
    SDL_Event e;
    SDL_zero(e);
    e.type = stdin_closed_event;
    SDL_PushEvent(&e);
    return 0;
}

static const char *json_bool(bool b) { return b ? "true" : "false"; }

int main(void)
{
    if (!SDL_Init(SDL_INIT_VIDEO)) {
        fprintf(stderr, "game-probe: SDL_Init: %s\n", SDL_GetError());
        return 1;
    }
    stdin_closed_event = SDL_RegisterEvents(1);

    SDL_Window *window = SDL_CreateWindow("game-probe", 1280, 720, SDL_WINDOW_FULLSCREEN);
    if (!window) {
        fprintf(stderr, "game-probe: SDL_CreateWindow: %s\n", SDL_GetError());
        SDL_Quit();
        return 1;
    }
    // a window shows only once something is drawn into it (wayland maps on the first buffer)
    SDL_Renderer *renderer = SDL_CreateRenderer(window, NULL);

    SDL_Thread *watcher = SDL_CreateThread(watch_stdin, "stdin", NULL);
    if (watcher) SDL_DetachThread(watcher);

    bool ready = false;
    bool fullscreen = false;
    bool running = true;
    int status = 0;
    Uint64 deadline = SDL_GetTicks() + READY_DEADLINE_MS;
    Uint64 next_try = 0;

    while (running) {
        SDL_Event e;
        if (SDL_WaitEventTimeout(&e, RETRY_MS)) {
            do {
                if (e.type == SDL_EVENT_QUIT || e.type == stdin_closed_event) {
                    running = false;
                } else if (e.type == SDL_EVENT_WINDOW_ENTER_FULLSCREEN || e.type == SDL_EVENT_WINDOW_LEAVE_FULLSCREEN) {
                    fullscreen = e.type == SDL_EVENT_WINDOW_ENTER_FULLSCREEN;
                } else if (e.type == SDL_EVENT_MOUSE_MOTION) {
                    printf("{\"rel\":[%g,%g],\"pos\":[%g,%g]}\n", e.motion.xrel, e.motion.yrel, e.motion.x, e.motion.y);
                    fflush(stdout);
                } else if (e.type == SDL_EVENT_MOUSE_BUTTON_DOWN || e.type == SDL_EVENT_MOUSE_BUTTON_UP) {
                    printf("{\"button\":%d,\"pressed\":%s}\n", e.button.button, json_bool(e.button.down));
                    fflush(stdout);
                }
            } while (running && SDL_PollEvent(&e));
        }
        if (!running) break;

        if (renderer) {
            SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);
            SDL_RenderClear(renderer);
            SDL_RenderPresent(renderer);
        }

        if (ready) continue;
        Uint64 now = SDL_GetTicks();
        bool focused = (SDL_GetWindowFlags(window) & SDL_WINDOW_INPUT_FOCUS) != 0;
        if (focused && now >= next_try) {
            // a compositor may refuse the lock until the window is focused and mapped, so keep asking
            if (!SDL_GetWindowRelativeMouseMode(window)) SDL_SetWindowRelativeMouseMode(window, true);
            next_try = now + RETRY_MS;
        }
        bool relative = SDL_GetWindowRelativeMouseMode(window);
        // fullscreen is asked for, but a compositor may map the window at its asked size first and make it fullscreen
        // later: ready once the compositor says it is, as the asked size is reported before that
        int w = 0, h = 0;
        SDL_Rect display = {0};
        SDL_GetWindowSize(window, &w, &h);
        SDL_GetDisplayBounds(SDL_GetDisplayForWindow(window), &display);
        if (focused && relative && fullscreen) {
            ready = true;
            printf("{\"ready\":true,\"relative\":%s,\"size\":[%d,%d]}\n", json_bool(relative), w, h);
            fflush(stdout);
        } else if (now >= deadline) {
            printf("{\"ready\":false,\"focus\":%s,\"relative\":%s,\"fullscreen\":%s,\"size\":[%d,%d],\"display\":[%d,%d]}\n",
                   json_bool(focused), json_bool(relative), json_bool(fullscreen), w, h, display.w, display.h);
            fflush(stdout);
            fprintf(stderr, "game-probe: no relative mode by the deadline: %s\n", SDL_GetError());
            status = 2;
            running = false;
        }
    }

    if (renderer) SDL_DestroyRenderer(renderer);
    SDL_DestroyWindow(window);
    SDL_Quit();
    return status;
}
