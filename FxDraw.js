"use strict";
// Adapter for World, parseSystem and uvRect from the installed FXPreview viewer.html.
const opts = {maxParticles: 2048, oneShotZero: false, surface: 'land'};
function prioVisible() { return true; }

const effects = new Map();
const emitters = new Map();
function installEffect(key, json) {
  const payload = JSON.parse(json);
  if (!payload.defs) throw new Error('找不到特效 ' + payload.id);
  const materials = [];
  for (const id of Object.keys(payload.templates)) {
    const template = payload.templates[id];
    const s = parseSystem(template);
    template.spriteIndex = materials.length;
    materials.push({texture:s.tex, columns:s.draw.framesPerRow || 1, frames:s.draw.totalFrames || 1,
      pass:s.draw.isDistort ? '折射' : s.draw.isLight ? '点光源' : '',
      shader:s.shader, ground:s.groundAligned, sort:s.sortLevel, geometry:s.draw.geometry});
  }
  effects.set(key, payload);
  return JSON.stringify({materials:materials, absent:payload.absent || []});
}
function startEmitter(handle, key, water) {
  opts.surface = water ? 'water' : 'land';
  const w = new World(effects.get(key), {seed:handle});
  w.reset();
  emitters.set(handle, w);
}
function stopEmitter(handle) { emitters.delete(handle); }

// Return an immutable snapshot in one interop call. Detached particles keep the
// emitter transform at birth, so a turning exhaust leaves its trail in space.
function sampleEmitter(handle, steps, m11,m12,m13,m21,m22,m23,m31,m32,m33,tx,ty,tz) {
  const w = emitters.get(handle);
  const pose = [m11,m12,m13,m21,m22,m23,m31,m32,m33,tx,ty,tz];
  for (let t = 0; t < steps; t++) w.step();
  let count = 0;
  for (const inst of w.insts) count += inst.particles.length;
  if (!count && w.insts.every(inst => inst.done || inst.dead)) return new Float32Array([-1]);
  const data = new Float32Array(count * 14);
  let i = 0;
  for (const inst of w.insts) {
    const s = inst.spec;
    if (s.draw.isLight || s.draw.isDistort) continue;
    for (const p of inst.particles) {
      if (!p.birthPose) p.birthPose = pose;
      const m = s.physics.attachBone ? pose : p.birthPose;
      const x = p.pos[0], y = p.pos[1], z = p.pos[2];
      const uv = uvRect(s,p);
      data[i++] = s.raw.spriteIndex;
      data[i++] = x*m[0]+y*m[3]+z*m[6]+m[9];
      data[i++] = x*m[1]+y*m[4]+z*m[7]+m[10];
      data[i++] = x*m[2]+y*m[5]+z*m[8]+m[11];
      data[i++] = p.size;
      data[i++] = s.groundAligned ? p.angXY : p.ang;
      data[i++] = p.rgb[0]*p.colorScale;
      data[i++] = p.rgb[1]*p.colorScale;
      data[i++] = p.rgb[2]*p.colorScale;
      data[i++] = clamp(p.alpha,0,1);
      data.set(uv,i); i += 4;
    }
  }
  return data.subarray(0,i);
}
