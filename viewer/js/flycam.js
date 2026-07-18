// The free camera, extracted whole from view3d: first-person look/pan/dolly
// on mouse, look/pan/pinch on touch, WASD flight with Source's spectator
// vertical binds (Space/Ctrl; Q/E/C stay as aliases), long-press and
// tap-vs-drag recognition. It owns every input gesture and the camera's
// yaw/pitch/velocity; what a tap MEANS (pick a marker, set a target, probe an
// origin) belongs to the caller via the callbacks - this module knows nothing
// about lineups.
//
// THREE is the lazily loaded window global, so nothing here may touch it at
// import time; createFlyCamera is only called after three.min.js has loaded.

import { isDrag } from "./state.js?v=12";

const LOOK_SENSITIVITY = 0.0035;
const PAN_SENSITIVITY = 1.35;
const DOLLY_SENSITIVITY = 1.4;
const PINCH_DOLLY_SENSITIVITY = 4.5;
const LONG_PRESS_MS = 450, LONG_PRESS_SLOP_PX = 8;

// Units per second, not per frame: a fixed per-frame step made the camera
// travel 2.4x further on a 144Hz monitor than a 60Hz one.
const SPEED = 430;
const FAST_SPEED = 1500;
// Rate the velocity closes on what the keys ask for. Deceleration is
// deliberately snappier than acceleration (the editor-viewport convention):
// in a measuring tool, releasing a key means "my eye goes exactly here" -
// coasting past the spot fights the whole point.
const ACCEL_RESPONSIVENESS = 11;
const DECEL_RESPONSIVENESS = 24;
const STOPPED = 1;

// Every code here must be preventDefault()'d: the browser owns several of
// these as chords before our handler ever runs them as game input - Space
// scrolls the page, and Ctrl+W (an entirely plausible chord once Ctrl means
// "down") closes the whole browser tab.
const FLY_KEYS = new Set([
  "KeyW", "KeyA", "KeyS", "KeyD", "KeyQ", "KeyE", "KeyC", "Space",
  "ControlLeft", "ControlRight", "ShiftLeft", "ShiftRight",
]);

export function createFlyCamera(camera, domElement, { onTap, onLongPress, requestRender, ignoreKeys }) {
  // Free-look: yaw/pitch of the camera's own facing direction (not an orbit
  // pivot far away). Positive pitch looks down (lookDir.z = -sin(pitch)),
  // the same sign convention the solver's pitch degrees use, so setLook()
  // takes lineup angles with no conversion. Seeded from wherever the caller
  // pointed the camera before construction.
  const initDir = new THREE.Vector3();
  camera.getWorldDirection(initDir);
  let yaw = Math.atan2(initDir.y, initDir.x);
  let pitch = -Math.asin(THREE.MathUtils.clamp(initDir.z, -1, 1));
  const PITCH_LIMIT = 89 * Math.PI / 180;
  const lookDir = new THREE.Vector3(), lookAt = new THREE.Vector3();
  function applyLook() {
    // The clamp keeps the view matrix from degenerating when the look
    // direction lines up with camera.up.
    pitch = Math.max(-PITCH_LIMIT, Math.min(PITCH_LIMIT, pitch));
    const cp = Math.cos(pitch);
    lookDir.set(cp * Math.cos(yaw), cp * Math.sin(yaw), -Math.sin(pitch));
    camera.lookAt(lookAt.copy(camera.position).add(lookDir));
    requestRender();
  }

  const panRight = new THREE.Vector3(), panUp = new THREE.Vector3(0, 0, 1), dollyDir = new THREE.Vector3();
  let down = null;
  let dragButton = -1;
  let lastX = 0, lastY = 0;
  let pressTimer = 0, pressConsumed = false;
  // Touch: one finger looks, two fingers pan (centroid) and dolly (pinch).
  // Touch can never produce a secondary button or a wheel event, so without
  // this pan and dolly would be mouse-only.
  const touches = new Map();
  let pinchDist = 0, pinchX = 0, pinchY = 0;

  function cancelLongPress() {
    if (pressTimer) {
      clearTimeout(pressTimer);
      pressTimer = 0;
    }
  }

  domElement.addEventListener("contextmenu", e => e.preventDefault());
  domElement.addEventListener("pointerdown", e => {
    if (e.pointerType === "touch") {
      touches.set(e.pointerId, [e.clientX, e.clientY]);
      if (touches.size === 2) {
        domElement.setPointerCapture(e.pointerId);
        cancelLongPress();
        dragButton = -1;
        down = null; // a pinch is never also a click
        domElement.classList.remove("looking");
        const [a, b] = [...touches.values()];
        pinchDist = Math.hypot(a[0] - b[0], a[1] - b[1]);
        pinchX = (a[0] + b[0]) / 2;
        pinchY = (a[1] + b[1]) / 2;
        domElement.classList.add("panning");
        return;
      }
      pressConsumed = false;
      pressTimer = setTimeout(() => {
        pressTimer = 0;
        pressConsumed = true;
        navigator.vibrate?.(10);
        onLongPress(e.clientX, e.clientY);
      }, LONG_PRESS_MS);
    }
    down = [e.clientX, e.clientY];
    if (e.button === 0 || e.button === 2) {
      dragButton = e.button;
      lastX = e.clientX;
      lastY = e.clientY;
      domElement.setPointerCapture(e.pointerId);
      domElement.classList.add(dragButton === 0 ? "looking" : "panning");
    }
  });
  domElement.addEventListener("pointermove", e => {
    if (e.pointerType === "touch" && touches.has(e.pointerId)) {
      touches.set(e.pointerId, [e.clientX, e.clientY]);
      if (touches.size === 2) {
        const [a, b] = [...touches.values()];
        const dist = Math.hypot(a[0] - b[0], a[1] - b[1]);
        const cx = (a[0] + b[0]) / 2, cy = (a[1] + b[1]) / 2;
        panRight.set(Math.sin(yaw), -Math.cos(yaw), 0);
        camera.position.addScaledVector(panRight, (cx - pinchX) * PAN_SENSITIVITY);
        camera.position.addScaledVector(panUp, (cy - pinchY) * PAN_SENSITIVITY);
        camera.getWorldDirection(dollyDir);
        camera.position.addScaledVector(dollyDir, (dist - pinchDist) * PINCH_DOLLY_SENSITIVITY);
        pinchDist = dist;
        pinchX = cx;
        pinchY = cy;
        requestRender();
        return;
      }
    }
    if (pressTimer && down && Math.hypot(e.clientX - down[0], e.clientY - down[1]) > LONG_PRESS_SLOP_PX) {
      cancelLongPress();
    }
    if (dragButton === -1) { return; }
    const dx = e.clientX - lastX, dy = e.clientY - lastY;
    lastX = e.clientX;
    lastY = e.clientY;
    if (dragButton === 0) {
      yaw -= dx * LOOK_SENSITIVITY;
      // Positive pitch looks down, so dragging the mouse down (dy > 0) -
      // standard non-inverted mouselook - must increase pitch.
      pitch += dy * LOOK_SENSITIVITY;
      applyLook();
    } else {
      panRight.set(Math.sin(yaw), -Math.cos(yaw), 0);
      camera.position.addScaledVector(panRight, dx * PAN_SENSITIVITY);
      camera.position.addScaledVector(panUp, dy * PAN_SENSITIVITY);
      requestRender();
    }
  });
  const endPointer = e => {
    touches.delete(e.pointerId);
    cancelLongPress();
    dragButton = -1;
    domElement.classList.remove("looking", "panning");
  };
  domElement.addEventListener("pointercancel", endPointer);
  domElement.addEventListener("pointerup", e => {
    const wasPinching = e.pointerType === "touch" && touches.size >= 2;
    endPointer(e);
    if (pressConsumed || wasPinching) { pressConsumed = false; return; }
    if (!down || isDrag(down[0], down[1], e.clientX, e.clientY)) { down = null; return; }
    down = null;
    onTap({ x: e.clientX, y: e.clientY, button: e.button });
  });
  domElement.addEventListener("wheel", e => {
    e.preventDefault();
    camera.getWorldDirection(dollyDir);
    camera.position.addScaledVector(dollyDir, -e.deltaY * DOLLY_SENSITIVITY);
    requestRender();
  }, { passive: false });

  // Key listeners bind in start() and unbind in stop() so 2D mode never sees
  // them; key events aimed at UI controls are ignored so typing or toggling
  // never flies the camera.
  const keys = new Set();
  const onKeyDown = e => {
    if (ignoreKeys?.(e)) {
      return;
    }
    if (FLY_KEYS.has(e.code)) {
      e.preventDefault();
    }
    keys.add(e.code);
  };
  const onKeyUp = e => keys.delete(e.code);
  const onBlur = () => keys.clear();

  // Reused across frames; tick() runs every frame while the camera still has
  // velocity, which outlasts the key press now that it eases to a stop.
  const fwd = new THREE.Vector3(), right = new THREE.Vector3();
  const wanted = new THREE.Vector3(), velocity = new THREE.Vector3();
  let lastFrame = 0;

  return {
    // Solver-convention angles in radians; lineup pitch/yaw degrees convert
    // with a bare PI/180 and no sign fiddling.
    setLook(yawRad, pitchRad) {
      yaw = yawRad;
      pitch = pitchRad;
      applyLook();
    },
    start() {
      window.addEventListener("keydown", onKeyDown);
      window.addEventListener("keyup", onKeyUp);
      window.addEventListener("blur", onBlur);
    },
    stop() {
      keys.clear();
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("keyup", onKeyUp);
      window.removeEventListener("blur", onBlur);
    },
    // Per-frame flight integration; call from the render loop.
    tick(now) {
      const dt = Math.min((now - lastFrame) / 1000, 0.1);
      lastFrame = now;
      if (keys.size === 0 && velocity.lengthSq() < STOPPED) {
        velocity.set(0, 0, 0);
        return;
      }
      // W/S fly along the full look direction - look down and W descends - so
      // dropping to a lower spot rarely needs the Ctrl "down" bind. Strafe (A/D)
      // stays level regardless of pitch, computed from the horizontal heading.
      camera.getWorldDirection(fwd);
      fwd.normalize();
      right.set(fwd.y, -fwd.x, 0);
      if (right.lengthSq() < 1e-6) { right.set(1, 0, 0); }
      right.normalize();
      wanted.set(0, 0, 0);
      if (keys.has("KeyW")) { wanted.add(fwd); }
      if (keys.has("KeyS")) { wanted.sub(fwd); }
      if (keys.has("KeyD")) { wanted.add(right); }
      if (keys.has("KeyA")) { wanted.sub(right); }
      if (keys.has("KeyE") || keys.has("Space")) { wanted.z += 1; }
      if (keys.has("KeyQ") || keys.has("KeyC") || keys.has("ControlLeft") || keys.has("ControlRight")) { wanted.z -= 1; }
      if (wanted.lengthSq() > 0) {
        const fast = keys.has("ShiftLeft") || keys.has("ShiftRight");
        wanted.normalize().multiplyScalar(fast ? FAST_SPEED : SPEED);
      }
      // Frame-rate independent exponential approach: the fraction of the
      // remaining gap closed depends on elapsed time, not frames elapsed.
      const responsiveness = wanted.lengthSq() > 0 ? ACCEL_RESPONSIVENESS : DECEL_RESPONSIVENESS;
      velocity.lerp(wanted, 1 - Math.exp(-responsiveness * dt));
      if (velocity.lengthSq() < STOPPED) {
        velocity.set(0, 0, 0);
        return;
      }
      camera.position.addScaledVector(velocity, dt);
      requestRender();
    },
  };
}
