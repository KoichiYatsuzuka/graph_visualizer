// raylib_bridge.cpp - implementation of raylib_bridge.h (see the header for the usage from Arrow).
//
// Each function forwards its arguments to the raylib function with the CamelCase name.
// raylib is linked statically (third_party/raylib/lib/raylib.lib), so raylib_bridge.dll does
// not need raylib.dll at run time. Build: raylib_bridge/build.ps1.
//
// raylib.h must be included before raylib_bridge.h: it defines RAYLIB_H, which makes
// raylib_bridge.h use raylib's Color / Vector2 instead of its own copies. The compiler then
// rejects any declaration in raylib_bridge.h that does not match a definition below.
#include <algorithm>
#include <cmath>
#include <cstddef>
#include <utility>
#include <vector>

#include "raylib.h"
#include "rlgl.h"

#include "raylib_bridge.h"

// Arrow lays out Color and Vector2 from raylib_bridge.h; these must stay the same as raylib's.
static_assert(sizeof(Color) == 4, "Color must be 4 bytes (unsigned char r, g, b, a)");
static_assert(sizeof(Vector2) == 8, "Vector2 must be 8 bytes (float x, y)");

// -- window and timing ---------------------------------------------------------

void set_config_flags(unsigned int flags) { SetConfigFlags(flags); }

void set_trace_log_level(int log_level) { SetTraceLogLevel(log_level); }

void init_window(int width, int height, const char* title) { InitWindow(width, height, title); }

void close_window(void) { CloseWindow(); }

bool window_should_close(void) { return WindowShouldClose(); }

bool is_window_ready(void) { return IsWindowReady(); }

void set_target_fps(int fps) { SetTargetFPS(fps); }

int get_screen_width(void) { return GetScreenWidth(); }

int get_screen_height(void) { return GetScreenHeight(); }

float get_frame_time(void) { return GetFrameTime(); }

double get_time(void) { return GetTime(); }

int get_fps(void) { return GetFPS(); }

// -- drawing -------------------------------------------------------------------

void begin_drawing(void) { BeginDrawing(); }

void end_drawing(void) { EndDrawing(); }

void clear_background(Color color) { ClearBackground(color); }

void draw_line_ex(Vector2 start_pos, Vector2 end_pos, float thick, Color color)
{
    DrawLineEx(start_pos, end_pos, thick, color);
}

void draw_circle_v(Vector2 center, float radius, Color color) { DrawCircleV(center, radius, color); }

void draw_circle_lines_v(Vector2 center, float radius, Color color)
{
    DrawCircleLinesV(center, radius, color);
}

void draw_rectangle_v(Vector2 position, Vector2 size, Color color) { DrawRectangleV(position, size, color); }

void draw_triangle(Vector2 v1, Vector2 v2, Vector2 v3, Color color) { DrawTriangle(v1, v2, v3, color); }

void draw_text(const char* text, int pos_x, int pos_y, int font_size, Color color)
{
    DrawText(text, pos_x, pos_y, font_size, color);
}

int measure_text(const char* text, int font_size) { return MeasureText(text, font_size); }

void draw_fps(int pos_x, int pos_y) { DrawFPS(pos_x, pos_y); }

// -- input ---------------------------------------------------------------------

Vector2 get_mouse_position(void) { return GetMousePosition(); }

bool is_mouse_button_pressed(int button) { return IsMouseButtonPressed(button); }

bool is_mouse_button_down(int button) { return IsMouseButtonDown(button); }

bool is_key_pressed(int key) { return IsKeyPressed(key); }

// -- graph drawing -----------------------------------------------------------------

namespace {

// Largest distance between the drawn circle edge and the true circle, in pixels.
constexpr double CIRCLE_MAX_EDGE_ERROR_PX = 0.1;
// Fewest edge segments of a filled circle (the same count as raylib's DrawCircleV).
constexpr int CIRCLE_MIN_SEGMENTS = 36;
// Arrow head length (and width) per pixel of shaft thickness.
constexpr float ARROW_HEAD_LENGTH_PER_THICKNESS = 4.0f;
// The head takes at most this fraction of the arrow length.
constexpr float ARROW_HEAD_MAX_LENGTH_RATIO = 0.5f;
// End points closer than this (pixels) have no direction, so no arrow is drawn.
constexpr double ARROW_MIN_CHORD_LENGTH_PX = 1.0e-3;
// An arc whose sagitta (bulge) is below this (pixels) is drawn as a straight segment.
constexpr double ARROW_STRAIGHT_SAGITTA_PX = 1.0e-2;
// Length of one straight piece approximating a curved shaft, and the cap on the number of pieces.
constexpr float ARROW_ARC_PIECE_LENGTH_PX = 4.0f;
constexpr int ARROW_ARC_MAX_PIECES = 1024;
// Where middle_color sits along a still (not flowing) shaft, as a fraction of the shaft length.
constexpr double ARROW_SHAFT_MIDDLE_FRACTION = 0.5;
// A flowing shaft shows the loop start -> middle -> end -> start once; each of the three color changes
// takes this fraction of the shaft.
constexpr int ARROW_FLOW_COLOR_COUNT = 3;
constexpr double ARROW_FLOW_STOP_SPACING = 1.0 / ARROW_FLOW_COLOR_COUNT;
// Joints of a shaft closer than this (fraction of the shaft) are merged.
constexpr double ARROW_JOINT_MERGE_FRACTION = 1.0e-9;
constexpr double PI_DOUBLE = 3.14159265358979323846;

// filled_circle_segment_count - number of edge segments so that the edge error is at most
// CIRCLE_MAX_EDGE_ERROR_PX.
//
// Arguments:
//   radius - circle radius in pixels (> 0).
// Returns:
//   the segment count, at least CIRCLE_MIN_SEGMENTS. One segment of angle theta deviates from
//   the circle by radius * (1 - cos(theta / 2)).
// Example:
//   filled_circle_segment_count(25.0f) == 36, filled_circle_segment_count(200.0f) == 100
int filled_circle_segment_count(float radius)
{
    const double cos_half_angle = 1.0 - CIRCLE_MAX_EDGE_ERROR_PX / static_cast<double>(radius);
    if (cos_half_angle <= 0.0) {
        return CIRCLE_MIN_SEGMENTS;
    }
    const double max_segment_angle = 2.0 * std::acos(cos_half_angle);
    const int segment_count = static_cast<int>(std::ceil(2.0 * PI_DOUBLE / max_segment_angle));
    return std::max(CIRCLE_MIN_SEGMENTS, segment_count);
}

// ArrowPath - centre line of an arrow: a straight segment or a circular arc from start to end.
//
// Members:
//   start, end (Vector2)     - end points in pixels.
//   is_arc (bool)            - true for a circular arc, false for a straight segment.
//   left_normal_x/_y (double)- unit vector to the left of start->end as seen on screen
//                              (y grows downward); the normal of a straight path.
//   center_x/_y (double)     - centre of the arc (only when is_arc).
//   radius (double)          - radius of the arc in pixels (only when is_arc).
//   start_angle (double)     - atan2 angle of start seen from the centre, radians (only when is_arc).
//   sweep_angle (double)     - signed angle from start to end, radians, |sweep_angle| <= pi (only when is_arc).
//                              Negative means counter-clockwise on screen, because y grows downward.
//   length (double)          - length of the centre line in pixels.
// Functions using it: make_arrow_path, arrow_path_point, arrow_path_normal, shaft_piece_count,
//   shaft_joint_fractions, draw_arrow_shaft, draw_arrow_head, draw_gradient_arrow.
struct ArrowPath {
    Vector2 start;
    Vector2 end;
    bool is_arc;
    double left_normal_x;
    double left_normal_y;
    double center_x;
    double center_y;
    double radius;
    double start_angle;
    double sweep_angle;
    double length;
};

// make_arrow_path - centre line from start to end with the given signed curvature.
//
// Arguments:
//   start, end - end points in pixels; they must be at least ARROW_MIN_CHORD_LENGTH_PX apart.
//   curvature  - 1 / R in 1/pixel (> 0 turns left on screen). |curvature| > 2 / distance is
//                clamped to the semicircle.
// Returns:
//   the path. A nearly straight arc (sagitta < ARROW_STRAIGHT_SAGITTA_PX) becomes a straight segment.
// Example:
//   make_arrow_path({0, 0}, {200, 0}, 0.01f)   // arc of R = 100 px bulging downward (to +y)
ArrowPath make_arrow_path(Vector2 start, Vector2 end, float curvature)
{
    ArrowPath path{};
    path.start = start;
    path.end = end;
    const double chord_x = static_cast<double>(end.x) - static_cast<double>(start.x);
    const double chord_y = static_cast<double>(end.y) - static_cast<double>(start.y);
    const double chord_length = std::sqrt(chord_x * chord_x + chord_y * chord_y);
    path.left_normal_x = chord_y / chord_length;
    path.left_normal_y = -chord_x / chord_length;

    const double half_chord = 0.5 * chord_length;
    const double semicircle_curvature = 1.0 / half_chord;
    const double clamped_curvature =
        std::clamp(static_cast<double>(curvature), -semicircle_curvature, semicircle_curvature);
    const double curvature_magnitude = std::fabs(clamped_curvature);
    // sagitta = R - sqrt(R^2 - c^2) = curvature * c^2 / (1 + sqrt(1 - (curvature * c)^2)), c = half_chord
    const double curvature_times_half_chord = std::min(1.0, curvature_magnitude * half_chord);
    const double sagitta = curvature_magnitude * half_chord * half_chord /
                           (1.0 + std::sqrt(1.0 - curvature_times_half_chord * curvature_times_half_chord));
    if (sagitta < ARROW_STRAIGHT_SAGITTA_PX) {
        path.is_arc = false;
        path.length = chord_length;
        return path;
    }

    path.is_arc = true;
    path.radius = 1.0 / curvature_magnitude;
    // Turning left puts the centre on the left of the chord; turning right, on the right.
    const double turn_sign = (clamped_curvature > 0.0) ? 1.0 : -1.0;
    const double center_offset = std::sqrt(std::max(0.0, path.radius * path.radius - half_chord * half_chord));
    path.center_x = 0.5 * (static_cast<double>(start.x) + static_cast<double>(end.x)) +
                    turn_sign * center_offset * path.left_normal_x;
    path.center_y = 0.5 * (static_cast<double>(start.y) + static_cast<double>(end.y)) +
                    turn_sign * center_offset * path.left_normal_y;
    path.start_angle = std::atan2(static_cast<double>(start.y) - path.center_y,
                                  static_cast<double>(start.x) - path.center_x);
    const double sweep_magnitude = 2.0 * std::asin(curvature_times_half_chord);
    // With y growing downward, counter-clockwise on screen (turning left) decreases the atan2 angle.
    path.sweep_angle = -turn_sign * sweep_magnitude;
    path.length = path.radius * sweep_magnitude;
    return path;
}

// arrow_path_point - point on the centre line at the given distance from the start.
//
// Arguments:
//   path        - the centre line (make_arrow_path).
//   arc_length  - distance from the start along the line, 0 .. path.length (pixels).
// Returns:
//   the point in pixels.
// Example:
//   arrow_path_point(path, path.length)   // == path.end (up to rounding)
Vector2 arrow_path_point(const ArrowPath& path, double arc_length)
{
    const double fraction = arc_length / path.length;
    if (!path.is_arc) {
        return Vector2{
            static_cast<float>(path.start.x + fraction * (static_cast<double>(path.end.x) - path.start.x)),
            static_cast<float>(path.start.y + fraction * (static_cast<double>(path.end.y) - path.start.y)),
        };
    }
    const double angle = path.start_angle + fraction * path.sweep_angle;
    return Vector2{
        static_cast<float>(path.center_x + path.radius * std::cos(angle)),
        static_cast<float>(path.center_y + path.radius * std::sin(angle)),
    };
}

// arrow_path_normal - unit vector perpendicular to the centre line at the given distance from the start.
//
// Arguments:
//   path, arc_length - as in arrow_path_point.
// Returns:
//   the normal; it points to the same side along the whole path (outward from the centre of an arc),
//   so offsetting by +/- half the thickness gives the two edges of the shaft without gaps at joints.
// Example:
//   const Vector2 normal = arrow_path_normal(path, 0.0);
Vector2 arrow_path_normal(const ArrowPath& path, double arc_length)
{
    if (!path.is_arc) {
        return Vector2{static_cast<float>(path.left_normal_x), static_cast<float>(path.left_normal_y)};
    }
    const double angle = path.start_angle + (arc_length / path.length) * path.sweep_angle;
    return Vector2{static_cast<float>(std::cos(angle)), static_cast<float>(std::sin(angle))};
}

// lerp_color - color between from_color (fraction 0) and to_color (fraction 1), channel by channel.
//
// Example:
//   lerp_color({255, 0, 0, 255}, {0, 0, 255, 255}, 0.5f)   // == {128, 0, 128, 255}
Color lerp_color(Color from_color, Color to_color, float fraction)
{
    const auto lerp_channel = [fraction](unsigned char from_value, unsigned char to_value) {
        const float value = static_cast<float>(from_value) +
                            fraction * (static_cast<float>(to_value) - static_cast<float>(from_value));
        return static_cast<unsigned char>(std::lround(std::clamp(value, 0.0f, 255.0f)));
    };
    return Color{
        lerp_channel(from_color.r, to_color.r),
        lerp_channel(from_color.g, to_color.g),
        lerp_channel(from_color.b, to_color.b),
        lerp_channel(from_color.a, to_color.a),
    };
}

// ShaftGradient - colors along a shaft; the color changes linearly between the color stops.
//
// Members:
//   start_color (Color)  - see below.
//   middle_color (Color) - see below.
//   end_color (Color)    - see below. The head is always end_color.
//   is_flowing (bool)    - false (still): start_color at the start point, middle_color at
//                          ARROW_SHAFT_MIDDLE_FRACTION of the shaft, end_color where the head begins.
//                          true (flowing): the shaft shows one cycle of the loop start -> middle -> end -> start
//                          (each change takes ARROW_FLOW_STOP_SPACING of the shaft), shifted toward the head
//                          by flow_phase.
//   flow_phase (double)  - shift of the loop in shaft lengths (cycles); only the fractional part matters.
//                          Used only when is_flowing; must be finite.
// Functions using it: shaft_gradient_color, shaft_color_stop_fractions, draw_arrow_shaft, draw_gradient_arrow.
struct ShaftGradient {
    Color start_color;
    Color middle_color;
    Color end_color;
    bool is_flowing;
    double flow_phase;
};

// fractional_part - x - floor(x), in [0, 1) (it can round up to 1 for a tiny negative x).
//
// Example:
//   fractional_part(1.25) == 0.25, fractional_part(-0.75) == 0.25
double fractional_part(double value) { return value - std::floor(value); }

// shaft_gradient_color - color of the shaft at the given fraction of its length.
//
// Arguments:
//   gradient - colors and mode of the shaft (ShaftGradient).
//   fraction - 0 (start point) .. 1 (where the head begins).
// Returns:
//   still:   start_color -> middle_color up to ARROW_SHAFT_MIDDLE_FRACTION, then middle_color -> end_color.
//   flowing: the color of the loop at fractional_part(fraction - flow_phase); the loop is continuous, so the
//            pattern has no seam when it wraps around.
// Example:
//   shaft_gradient_color({red, green, blue, false, 0.0}, 0.25)   // == {128, 128, 0, 255} (red and green mixed)
//   shaft_gradient_color({red, green, blue, true, 0.25}, 0.25)   // == red (the loop start moved to 1/4)
Color shaft_gradient_color(const ShaftGradient& gradient, double fraction)
{
    if (!gradient.is_flowing) {
        if (fraction <= ARROW_SHAFT_MIDDLE_FRACTION) {
            return lerp_color(gradient.start_color, gradient.middle_color,
                              static_cast<float>(fraction / ARROW_SHAFT_MIDDLE_FRACTION));
        }
        return lerp_color(gradient.middle_color, gradient.end_color,
                          static_cast<float>((fraction - ARROW_SHAFT_MIDDLE_FRACTION) / (1.0 - ARROW_SHAFT_MIDDLE_FRACTION)));
    }
    // Reduce the phase first, so that a large flow_phase does not eat the precision of fraction.
    const double loop_position = fractional_part(fraction - fractional_part(gradient.flow_phase));
    const double stop_position = loop_position / ARROW_FLOW_STOP_SPACING;  // 0 .. ARROW_FLOW_COLOR_COUNT
    if (stop_position < 1.0) {
        return lerp_color(gradient.start_color, gradient.middle_color, static_cast<float>(stop_position));
    }
    if (stop_position < 2.0) {
        return lerp_color(gradient.middle_color, gradient.end_color, static_cast<float>(stop_position - 1.0));
    }
    return lerp_color(gradient.end_color, gradient.start_color, static_cast<float>(std::min(1.0, stop_position - 2.0)));
}

// shaft_color_stop_fractions - fractions of the shaft where a color of the gradient is reached exactly.
//
// Arguments:
//   gradient - colors and mode of the shaft (ShaftGradient).
// Returns:
//   the fractions in [0, 1), unsorted. draw_arrow_shaft puts a joint at each of them; without a joint the
//   color would be interpolated across it and never reached.
// Example:
//   shaft_color_stop_fractions({red, green, blue, true, 0.25})   // == {0.25, 0.5833..., 0.9166...}
std::vector<double> shaft_color_stop_fractions(const ShaftGradient& gradient)
{
    if (!gradient.is_flowing) {
        return {ARROW_SHAFT_MIDDLE_FRACTION};
    }
    std::vector<double> stop_fractions;
    const double phase_fraction = fractional_part(gradient.flow_phase);
    for (int stop_index = 0; stop_index < ARROW_FLOW_COLOR_COUNT; ++stop_index) {
        stop_fractions.push_back(fractional_part(stop_index * ARROW_FLOW_STOP_SPACING + phase_fraction));
    }
    return stop_fractions;
}

// shaft_piece_count - number of equal straight pieces that approximate the centre line of a shaft.
//
// Arguments:
//   path         - the centre line (make_arrow_path).
//   shaft_length - length of the shaft along the path in pixels.
// Returns:
//   1 for a straight shaft; about one piece per ARROW_ARC_PIECE_LENGTH_PX for an arc, at most
//   ARROW_ARC_MAX_PIECES. draw_arrow_shaft adds joints at the color stops on top of these.
// Example:
//   shaft_piece_count(arc_path, 123.1)   // == 31
int shaft_piece_count(const ArrowPath& path, double shaft_length)
{
    if (!path.is_arc) {
        return 1;
    }
    // Cap in double before converting, so that a huge length cannot overflow int.
    const double length_based_count =
        std::min(std::ceil(shaft_length / ARROW_ARC_PIECE_LENGTH_PX), static_cast<double>(ARROW_ARC_MAX_PIECES));
    return std::max(1, static_cast<int>(length_based_count));
}

// shaft_joint_fractions - sorted fractions of the shaft (0 .. 1, both included) where pieces join.
//
// Arguments:
//   path, shaft_length - as in shaft_piece_count.
//   gradient           - colors and mode of the shaft; its color stops become joints.
// Returns:
//   the equal division of shaft_piece_count plus the color stops, sorted, without near duplicates.
// Example:
//   shaft_joint_fractions(straight_path, 108.0, {red, green, blue, false, 0.0})   // == {0, 0.5, 1}
std::vector<double> shaft_joint_fractions(const ArrowPath& path, double shaft_length, const ShaftGradient& gradient)
{
    const int piece_count = shaft_piece_count(path, shaft_length);
    std::vector<double> joint_fractions;
    for (int piece_index = 0; piece_index <= piece_count; ++piece_index) {
        joint_fractions.push_back(static_cast<double>(piece_index) / piece_count);
    }
    for (const double stop_fraction : shaft_color_stop_fractions(gradient)) {
        if (stop_fraction > 0.0 && stop_fraction < 1.0) {
            joint_fractions.push_back(stop_fraction);
        }
    }
    std::sort(joint_fractions.begin(), joint_fractions.end());
    const auto is_near_duplicate = [](double earlier, double later) {
        return later - earlier < ARROW_JOINT_MERGE_FRACTION;
    };
    joint_fractions.erase(std::unique(joint_fractions.begin(), joint_fractions.end(), is_near_duplicate),
                          joint_fractions.end());
    return joint_fractions;
}

// emit_front_facing_triangle - add one triangle with per-vertex colors to raylib's batch.
//
// Purpose:
//   raylib culls back faces, and with its 2D projection (y grows downward) only triangles listed
//   counter-clockwise on screen are drawn (the same rule as DrawTriangle). The vertices are
//   reordered here, so callers may list them in either order.
// Must be called between rlBegin(RL_TRIANGLES) and rlEnd().
// Example:
//   emit_front_facing_triangle({0, 0}, red, {10, 0}, red, {0, 10}, blue);
void emit_front_facing_triangle(Vector2 point_a, Color color_a, Vector2 point_b, Color color_b,
                                Vector2 point_c, Color color_c)
{
    const float cross = (point_b.x - point_a.x) * (point_c.y - point_a.y) -
                        (point_b.y - point_a.y) * (point_c.x - point_a.x);
    if (cross > 0.0f) {
        std::swap(point_b, point_c);
        std::swap(color_b, color_c);
    }
    rlColor4ub(color_a.r, color_a.g, color_a.b, color_a.a);
    rlVertex2f(point_a.x, point_a.y);
    rlColor4ub(color_b.r, color_b.g, color_b.b, color_b.a);
    rlVertex2f(point_b.x, point_b.y);
    rlColor4ub(color_c.r, color_c.g, color_c.b, color_c.a);
    rlVertex2f(point_c.x, point_c.y);
}

// draw_arrow_shaft - draw the shaft from arc length 0 to shaft_length with a color gradient.
//
// Arguments:
//   path         - the centre line (make_arrow_path).
//   shaft_length - where the shaft ends (the head begins), pixels along the path.
//   thickness    - shaft width in pixels.
//   gradient     - colors and mode of the shaft (ShaftGradient). Each color stop is a joint between pieces
//                  (shaft_joint_fractions), so the stop colors are reached exactly.
// Example:
//   draw_arrow_shaft(path, path.length - head_length, 8.0f, ShaftGradient{red, green, blue, false, 0.0});
void draw_arrow_shaft(const ArrowPath& path, double shaft_length, float thickness, const ShaftGradient& gradient)
{
    const std::vector<double> joint_fractions = shaft_joint_fractions(path, shaft_length, gradient);
    const float half_thickness = 0.5f * thickness;
    rlBegin(RL_TRIANGLES);
    for (std::size_t joint_index = 0; joint_index + 1 < joint_fractions.size(); ++joint_index) {
        const double fraction_from = joint_fractions[joint_index];
        const double fraction_to = joint_fractions[joint_index + 1];
        const Vector2 center_from = arrow_path_point(path, fraction_from * shaft_length);
        const Vector2 center_to = arrow_path_point(path, fraction_to * shaft_length);
        const Vector2 normal_from = arrow_path_normal(path, fraction_from * shaft_length);
        const Vector2 normal_to = arrow_path_normal(path, fraction_to * shaft_length);
        const Color color_from = shaft_gradient_color(gradient, fraction_from);
        const Color color_to = shaft_gradient_color(gradient, fraction_to);

        const Vector2 edge_a_from = {center_from.x + half_thickness * normal_from.x,
                                     center_from.y + half_thickness * normal_from.y};
        const Vector2 edge_b_from = {center_from.x - half_thickness * normal_from.x,
                                     center_from.y - half_thickness * normal_from.y};
        const Vector2 edge_a_to = {center_to.x + half_thickness * normal_to.x,
                                   center_to.y + half_thickness * normal_to.y};
        const Vector2 edge_b_to = {center_to.x - half_thickness * normal_to.x,
                                   center_to.y - half_thickness * normal_to.y};
        emit_front_facing_triangle(edge_a_from, color_from, edge_b_from, color_from, edge_b_to, color_to);
        emit_front_facing_triangle(edge_a_from, color_from, edge_b_to, color_to, edge_a_to, color_to);
    }
    rlEnd();
}

// draw_arrow_head - draw the head: a triangle with its tip at the end point and its base centred on
// the centre line head_length before the end.
//
// Arguments:
//   path        - the centre line (make_arrow_path).
//   head_length - length of the head along the path in pixels (also the width of its base).
//   head_color  - fill color.
// Example:
//   draw_arrow_head(path, 32.0, blue);
void draw_arrow_head(const ArrowPath& path, double head_length, Color head_color)
{
    const Vector2 tip = path.end;
    const Vector2 base_center = arrow_path_point(path, path.length - head_length);
    const float axis_x = tip.x - base_center.x;
    const float axis_y = tip.y - base_center.y;
    const float axis_length = std::sqrt(axis_x * axis_x + axis_y * axis_y);
    if (axis_length <= 0.0f) {
        return;
    }
    const float half_base_width = 0.5f * static_cast<float>(head_length);
    const float side_x = axis_y / axis_length * half_base_width;
    const float side_y = -axis_x / axis_length * half_base_width;
    rlBegin(RL_TRIANGLES);
    emit_front_facing_triangle(tip, head_color, Vector2{base_center.x + side_x, base_center.y + side_y},
                               head_color, Vector2{base_center.x - side_x, base_center.y - side_y}, head_color);
    rlEnd();
}

// draw_gradient_arrow - common body of draw_curved_arrow_three_colors and draw_curved_arrow_flowing.
//
// Arguments:
//   start_x, start_y, end_x, end_y, thickness, curvature - as in draw_curved_arrow (raylib_bridge.h).
//   gradient - colors and mode of the shaft (ShaftGradient); the head is gradient.end_color.
// Purpose:
//   rejects the inputs that cannot be drawn (non-finite coordinates, thickness <= 0, NaN curvature, the same
//   two points), then draws the shaft and the head.
// Example:
//   draw_gradient_arrow(100.0f, 300.0f, 400.0f, 300.0f, 6.0f, ShaftGradient{red, green, blue, false, 0.0}, 0.005f);
void draw_gradient_arrow(float start_x, float start_y, float end_x, float end_y, float thickness,
                         const ShaftGradient& gradient, float curvature)
{
    const bool coordinates_finite = std::isfinite(start_x) && std::isfinite(start_y) && std::isfinite(end_x) &&
                                    std::isfinite(end_y);
    if (!coordinates_finite || !(thickness > 0.0f) || !std::isfinite(thickness) || std::isnan(curvature)) {
        return;
    }
    const double chord_length = std::hypot(static_cast<double>(end_x) - start_x, static_cast<double>(end_y) - start_y);
    if (chord_length < ARROW_MIN_CHORD_LENGTH_PX) {
        return;
    }

    const ArrowPath path = make_arrow_path(Vector2{start_x, start_y}, Vector2{end_x, end_y}, curvature);
    const double head_length = std::min(static_cast<double>(ARROW_HEAD_LENGTH_PER_THICKNESS * thickness),
                                        ARROW_HEAD_MAX_LENGTH_RATIO * path.length);
    draw_arrow_shaft(path, path.length - head_length, thickness, gradient);
    draw_arrow_head(path, head_length, gradient.end_color);
}

} // namespace

// draw_filled_circle - see raylib_bridge.h.
//
// Example (Arrow):
//   rl.draw_filled_circle(400.0, 300.0, 25.0, rl.Color(230, 41, 55, 255))
void draw_filled_circle(float center_x, float center_y, float radius, Color color)
{
    if (!(radius > 0.0f)) {  // also rejects NaN
        return;
    }
    DrawCircleSector(Vector2{center_x, center_y}, radius, 0.0f, 360.0f, filled_circle_segment_count(radius), color);
}

// draw_curved_arrow - see raylib_bridge.h for the meaning of the arguments.
//
// Purpose:
//   draws an edge of the reaction graph: a shaft along a circular arc (or a straight line) with a
//   start->end color gradient, and a head of end_color whose tip is at the end point.
//   It is draw_curved_arrow_three_colors with the middle color halfway between start_color and end_color.
// Example (Arrow):
//   rl.draw_curved_arrow(100.0, 300.0, 400.0, 300.0, 6.0, rl.Color(230, 41, 55, 255), rl.Color(0, 121, 241, 255), 0.005)
void draw_curved_arrow(float start_x, float start_y, float end_x, float end_y, float thickness, Color start_color,
                       Color end_color, float curvature)
{
    const Color middle_color = lerp_color(start_color, end_color, 0.5f);
    draw_curved_arrow_three_colors(start_x, start_y, end_x, end_y, thickness, start_color, middle_color, end_color,
                                   curvature);
}

// draw_curved_arrow_three_colors - see raylib_bridge.h for the meaning of the arguments.
//
// Purpose:
//   draw_curved_arrow with a start -> middle -> end gradient on the shaft, so that the direction of the
//   edge stands out (e.g. pale -> vivid -> dark). The head is end_color.
// Example (Arrow):
//   rl.draw_curved_arrow_three_colors(100.0, 300.0, 400.0, 300.0, 6.0, rl.Color(200, 200, 255, 255), rl.Color(0, 121, 241, 255), rl.Color(0, 30, 90, 255), 0.005)
void draw_curved_arrow_three_colors(float start_x, float start_y, float end_x, float end_y, float thickness,
                                    Color start_color, Color middle_color, Color end_color, float curvature)
{
    const ShaftGradient still_gradient = {start_color, middle_color, end_color, false, 0.0};
    draw_gradient_arrow(start_x, start_y, end_x, end_y, thickness, still_gradient, curvature);
}

// draw_curved_arrow_flowing - see raylib_bridge.h for the meaning of the arguments.
//
// Purpose:
//   draw_curved_arrow_three_colors whose colors flow from the start toward the head when it is called every
//   frame with a growing flow_phase, so that the direction of the edge is shown by motion.
// Example (Arrow), one cycle every 2 seconds:
//   rl.draw_curved_arrow_flowing(100.0, 300.0, 400.0, 300.0, 6.0, rl.Color(200, 20, 20, 255), rl.Color(255, 190, 60, 255), rl.Color(20, 20, 200, 255), 0.005, rl.get_time() / 2.0)
void draw_curved_arrow_flowing(float start_x, float start_y, float end_x, float end_y, float thickness,
                               Color start_color, Color middle_color, Color end_color, float curvature,
                               double flow_phase)
{
    if (!std::isfinite(flow_phase)) {
        return;
    }
    const ShaftGradient flowing_gradient = {start_color, middle_color, end_color, true, flow_phase};
    draw_gradient_arrow(start_x, start_y, end_x, end_y, thickness, flowing_gradient, curvature);
}

// -- bridge-only helpers ---------------------------------------------------------

// read_screen_pixel - color of the pixel (x, y) drawn so far in the current frame.
//
// Arguments:
//   x, y - render pixels from the top-left corner of the window.
// Returns:
//   the RGBA color of the pixel (alpha is always 255, see rlReadScreenPixels), or
//   Color{0, 0, 0, 0} when (x, y) is outside the framebuffer.
// Purpose:
//   lets the Arrow tests check what was actually drawn. raylib keeps draw calls in a batch,
//   so the batch is flushed first; the framebuffer is read before end_drawing swaps it.
//   The render size (not the screen size) is read, because raylib's LoadImageFromScreen
//   multiplies the screen size by the DPI scale and reads outside the framebuffer when the
//   window is not HighDPI on a scaled display.
// Example (Arrow):
//   rl.begin_drawing()
//   rl.clear_background(rl.Color(245, 245, 245, 255))
//   let pixel = rl.read_screen_pixel(10, 10) mustbe rl.Color   # pixel.r == 245
//   rl.end_drawing()
Color read_screen_pixel(int x, int y)
{
    const Color outside_color = {0, 0, 0, 0};
    const int render_width = GetRenderWidth();
    const int render_height = GetRenderHeight();
    if (x < 0 || y < 0 || x >= render_width || y >= render_height) {
        return outside_color;
    }

    rlDrawRenderBatchActive();
    // RGBA8, rows already flipped so that row 0 is the top of the window.
    unsigned char* framebuffer_pixels = rlReadScreenPixels(render_width, render_height);
    if (framebuffer_pixels == nullptr) {
        return outside_color;
    }
    const std::size_t pixel_offset =
        4 * (static_cast<std::size_t>(y) * static_cast<std::size_t>(render_width) + static_cast<std::size_t>(x));
    const Color pixel_color = {
        framebuffer_pixels[pixel_offset + 0],
        framebuffer_pixels[pixel_offset + 1],
        framebuffer_pixels[pixel_offset + 2],
        framebuffer_pixels[pixel_offset + 3],
    };
    MemFree(framebuffer_pixels);
    return pixel_color;
}
