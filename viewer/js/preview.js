// First-person lineup previews: render one frame from a throw's exact eye
// position and aim, and snapshot it as a PNG data URL. Everything runs in the
// user's own browser against the shared camera/canvas the interactive view
// owns - no server round-trip.

import { EYE_HEIGHT_BY_TYPE, DEFAULT_EYE_HEIGHT } from "./state.js?v=15";
import { ensure3d, current3d, verticalFovFromDesired, ensureCrosshair } from "./view3d.js?v=15";
import { ensureTexturedScene, currentTexturedScene } from "./textured-scene.js?v=15";

// Renders one first-person frame from a lineup's exact throw position and
// angle - what the player would line their crosshair against, not the
// orbiting overview camera. Used for headless preview capture (screenshots
// are taken by the automation driving the browser, not by this function).
// fovDesiredDeg matches the client's fov_desired convar (90 is the CS2/CS:GO
// default); pass the player's actual setting if they have changed it.
export function renderPreview({ feet, type, pitchDeg, yawDeg, fovDesiredDeg = 90 }) {
  const three = current3d();
  if (!three) {
    throw new Error("renderPreview called before ensure3d()");
  }
  // The interactive view's own requestAnimationFrame loop (started by
  // three.start(), e.g. from clicking the 3D button) keeps re-rendering the
  // flat scene every frame with whatever camera state currently exists -
  // it will silently clobber this function's render within milliseconds if
  // left running, overwriting it before a screenshot can ever see it.
  three.stop();
  const { renderer, camera, markerGroup, targetGroup } = three;
  // Prefer the real-textured scene once ensureTexturedScene() has resolved;
  // fall back to the flat collision mesh otherwise so this still works
  // stand-alone (e.g. tests, or a map with no textured export yet).
  const scene = currentTexturedScene() ?? three.scene;
  const eyeHeight = EYE_HEIGHT_BY_TYPE[type] ?? DEFAULT_EYE_HEIGHT;
  const eye = new THREE.Vector3(feet[0], feet[1], feet[2] + eyeHeight);
  const pr = pitchDeg * Math.PI / 180, yr = yawDeg * Math.PI / 180;
  // Same convention as GrenadeTrajectory/AimReference: yaw around Z, pitch
  // tilts toward +Z as it goes negative (throws aim "down" at negative pitch).
  const dir = new THREE.Vector3(
    Math.cos(pr) * Math.cos(yr), Math.cos(pr) * Math.sin(yr), -Math.sin(pr));

  camera.fov = verticalFovFromDesired(fovDesiredDeg);
  camera.position.copy(eye);
  camera.lookAt(eye.clone().add(dir));
  camera.updateProjectionMatrix();

  // A clean shot of the world only: our own marker/target overlays would
  // never appear in a real player's view.
  const wasMarkerVisible = markerGroup.visible, wasTargetVisible = targetGroup.visible;
  markerGroup.visible = false;
  targetGroup.visible = false;
  renderer.render(scene, camera);
  markerGroup.visible = wasMarkerVisible;
  targetGroup.visible = wasTargetVisible;

  // The crosshair sits at the exact viewport center regardless of resolution,
  // matching where CS2 always draws it: it represents the aim direction the
  // camera is already looking along, not a 3D-projected world point.
  ensureCrosshair();
}

// Client-side lineup preview: everything renderPreview() needs already runs
// in the user's own browser, so no server or headless automation is needed
// to show one. Temporarily borrows the shared camera/canvas, snapshots it as
// a PNG data URL, then restores whatever the user was looking at (position,
// FOV, and the interactive loop if it was running) exactly as it was.
//
// The crosshair itself is an HTML overlay (ensureCrosshair()), not part of
// the canvas's own pixels, so a page-level screenshot (the headless capture
// path) picks it up for free but canvas.toDataURL() here would not - it is
// redrawn with 2D primitives onto a copy of the frame instead.
// Fixed 16:9 capture resolution: stage3d is usually display:none at this
// point (no one has opened the 3D view), so its clientWidth/Height read 0
// and resize3d() would leave the canvas at its tiny default size. A fixed
// size also makes every preview the same shape regardless of the browser
// window, rather than however wide the page happened to be.
const PREVIEW_WIDTH = 1600, PREVIEW_HEIGHT = 900;

export async function capturePreview({ feet, type, pitchDeg, yawDeg, fovDesiredDeg = 90 }) {
  const three = await ensure3d();
  await ensureTexturedScene();
  const wasLive = three.isLive;
  const cam = three.camera;
  const savedPos = cam.position.clone();
  const savedQuat = cam.quaternion.clone();
  const savedFov = cam.fov;
  const savedAspect = cam.aspect;

  three.renderer.setSize(PREVIEW_WIDTH, PREVIEW_HEIGHT, false);
  cam.aspect = PREVIEW_WIDTH / PREVIEW_HEIGHT;
  renderPreview({ feet, type, pitchDeg, yawDeg, fovDesiredDeg });
  const src = three.renderer.domElement;
  const out = document.createElement("canvas");
  out.width = src.width;
  out.height = src.height;
  const ctx = out.getContext("2d");
  ctx.drawImage(src, 0, 0);
  // CS2's default reticle (four gapped arms with a black outline), matching the
  // live 3D crosshair so the captured frame lines up the same way in game.
  const cx = out.width / 2, cy = out.height / 2;
  const arm = out.width / 130, gap = out.width / 300;
  const drawReticle = () => {
    ctx.beginPath();
    ctx.moveTo(cx, cy - gap - arm); ctx.lineTo(cx, cy - gap);
    ctx.moveTo(cx, cy + gap); ctx.lineTo(cx, cy + gap + arm);
    ctx.moveTo(cx - gap - arm, cy); ctx.lineTo(cx - gap, cy);
    ctx.moveTo(cx + gap, cy); ctx.lineTo(cx + gap + arm, cy);
    ctx.stroke();
  };
  const green = Math.max(1.5, out.width / 750);
  ctx.lineCap = "butt";
  ctx.strokeStyle = "rgba(0,0,0,0.9)";
  ctx.lineWidth = green + 2;
  drawReticle();
  ctx.strokeStyle = "#00ff00";
  ctx.lineWidth = green;
  drawReticle();
  const dataUrl = out.toDataURL("image/png");

  cam.position.copy(savedPos);
  cam.quaternion.copy(savedQuat);
  cam.fov = savedFov;
  cam.aspect = savedAspect;
  cam.updateProjectionMatrix();
  // Resyncs the canvas to stage3d's real size (0 if the interactive 3D view
  // was never opened, in which case this is a harmless no-op - resize3d()
  // itself guards on that and start() will size it again if 3D mode opens
  // later).
  three.resize3d();
  if (wasLive) {
    three.start();
  }
  return dataUrl;
}
