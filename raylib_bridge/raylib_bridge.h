// raylib_bridge.h - C API of raylib_bridge.dll, which lets Arrow draw with raylib.
//
// Arrow reads this header as the type stub of raylib_bridge.dll:
//
//     import[cpp-dll] raylib_bridge.raylib_bridge as rl      # from main.ar
//     import[cpp-dll] ..raylib_bridge.raylib_bridge as rl    # from tests/*.ar
//
// Most functions are thin wrappers of the raylib function with the CamelCase name
// (init_window -> InitWindow, draw_circle_v -> DrawCircleV), so the raylib cheatsheet describes
// the arguments: https://www.raylib.com/cheatsheet/cheatsheet.html
// The functions in the "graph drawing" and "bridge-only helpers" sections are not in raylib.
//
// Why a bridge instead of importing raylib.h itself:
//   * Arrow does not expand macros, so raylib's `RLAPI void InitWindow(...)` is read as a
//     function returning the unknown struct `RLAPI void`.
//   * raylib.h has a struct field named `type` (AutomationEvent), which breaks the Rust relay
//     DLL that Arrow generates for import[cpp-dll].
//
// Rules for editing this file (Arrow's header parser is not a full C preprocessor):
//   * ASCII only.
//   * No macros in front of declarations (the DLL exports come from raylib_bridge.def).
//   * Strings are `const char*`: pass Arrow `bytes`, e.g. "text".encode("utf-8").
//     raylib's default font has ASCII glyphs only.
//   * Structs passed by value must have only plain number fields (no arrays, no nested structs).
//   * After adding a function, also add it to raylib_bridge.cpp and raylib_bridge.def,
//     then run build.ps1.
#ifndef RAYLIB_BRIDGE_H
#define RAYLIB_BRIDGE_H

#ifndef __cplusplus
#include <stdbool.h>
#endif

// Color and Vector2 have the same layout as raylib's. When raylib_bridge.cpp is compiled,
// raylib.h is included first (it defines RAYLIB_H) and raylib's own definitions are used,
// so the compiler checks that the declarations below match the implementation.
//
// Color:   r, g, b, a  - 0..255 each (Arrow: rl.Color(230, 41, 55, 255))
// Vector2: x, y        - pixels from the top-left corner of the window (Arrow: rl.Vector2(10.0, 20.0))
#ifndef RAYLIB_H
typedef struct { unsigned char r, g, b, a; } Color;
typedef struct { float x, y; } Vector2;
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Glue classes
class Position_CPP{
public:
    float x;
    float y;

    Position_CPP(float _x, float _y){
        x = _x;
        y = _y;
    }
};

// -- window and timing ---------------------------------------------------------
// Call set_config_flags (optional) -> init_window -> ... -> close_window, in this order.
// The flag and log-level values are in raylib_bridge/raylib_consts.ar.
void set_config_flags(unsigned int flags);                       // window flags; call before init_window
void set_trace_log_level(int log_level);                         // hide raylib's INFO logs with LOG_WARNING
void init_window(int width, int height, const char* title);      // open the window and the OpenGL context
void close_window(void);                                         // close the window and release the context
bool window_should_close(void);                                  // true after the close button or ESC
bool is_window_ready(void);                                      // true between init_window and close_window
void set_target_fps(int fps);                                    // end_drawing waits to keep this frame rate
int get_screen_width(void);                                      // window width in pixels
int get_screen_height(void);                                     // window height in pixels
float get_frame_time(void);                                      // seconds spent on the last frame
double get_time(void);                                           // seconds since init_window
int get_fps(void);                                               // current frames per second

// -- drawing -------------------------------------------------------------------
// Draw only between begin_drawing and end_drawing.
void begin_drawing(void);
void end_drawing(void);                                          // show the frame and poll input events
void clear_background(Color color);
void draw_line_ex(Vector2 start_pos, Vector2 end_pos, float thick, Color color);
void draw_circle_v(Vector2 center, float radius, Color color);
void draw_circle_lines_v(Vector2 center, float radius, Color color);
void draw_rectangle_v(Vector2 position, Vector2 size, Color color);  // position = top-left corner
void draw_triangle(Vector2 v1, Vector2 v2, Vector2 v3, Color color); // vertices counter-clockwise on screen
void draw_text(const char* text, int pos_x, int pos_y, int font_size, Color color);
int measure_text(const char* text, int font_size);               // width of the text in pixels
void draw_fps(int pos_x, int pos_y);

// -- input ---------------------------------------------------------------------
Vector2 get_mouse_position(void);
bool is_mouse_button_pressed(int button);                        // true only on the frame of the press
bool is_mouse_button_down(int button);                           // true while the button is held
bool is_key_pressed(int key);

// -- graph drawing (bridge only, not in raylib) ----------------------------------
// Draw only between begin_drawing and end_drawing. Coordinates and lengths are absolute
// window pixels; (0, 0) is the top-left corner and y grows downward.
//
// draw_filled_circle: filled circle centred at (center_x, center_y).
//   radius: in pixels. Nothing is drawn when radius <= 0.
//   Unlike draw_circle_v, the number of edge segments grows with the radius, so the edge stays
//   within 0.1 px of the true circle at any size (at least 36 segments).
void draw_filled_circle(float center_x, float center_y, float radius, Color color);

// draw_curved_arrow: arrow from (start_x, start_y) to (end_x, end_y); the tip of the head is at the end point.
//   thickness:   width of the shaft in pixels. The head is 4 * thickness long and 4 * thickness wide,
//                but at most half of the arrow length long (short arrows keep a shaft).
//   start_color, end_color: the shaft changes gradually from start_color at the start point to end_color
//                where the head begins; the head is end_color.
//   curvature:   1 / R of the circular arc, in 1/pixel. 0 draws a straight arrow.
//                curvature > 0 turns left (counter-clockwise on screen) while going from start to end,
//                so the arc bulges to the right-hand side of the start->end direction; curvature < 0 turns
//                right. A->B and B->A drawn with the same curvature bulge to opposite sides.
//                |curvature| > 2 / distance (no arc of that radius passes both points) is clamped to the
//                semicircle (2 / distance).
//   Nothing is drawn when thickness <= 0, the two points are the same, or an argument is NaN.
void draw_curved_arrow(float start_x, float start_y, float end_x, float end_y, float thickness, Color start_color, Color end_color, float curvature);

// -- bridge-only helpers (not in raylib) -----------------------------------------
// read_screen_pixel: color of the pixel (x, y) drawn so far in the current frame.
//   Call it between begin_drawing and end_drawing. (0, 0) is the top-left corner; the
//   coordinates are render pixels (the same as screen pixels unless FLAG_WINDOW_HIGHDPI).
//   Returns Color(0, 0, 0, 0) when (x, y) is outside the window. It reads the whole
//   framebuffer, so use it in tests, not every frame.
Color read_screen_pixel(int x, int y);

#ifdef __cplusplus
}
#endif

#endif // RAYLIB_BRIDGE_H
