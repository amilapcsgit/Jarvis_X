import * as THREE from './assets/three.module.js';
import { EffectComposer } from './assets/three-addons/postprocessing/EffectComposer.js';
import { RenderPass } from './assets/three-addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from './assets/three-addons/postprocessing/UnrealBloomPass.js';
import { OutputPass } from './assets/three-addons/postprocessing/OutputPass.js';

// One perspective scene spans the viewport. The DOM remains the source of truth
// for the nucleus position; the orbital paths are intentionally not panel-clipped.
const canvas = document.querySelector('#ambient-canvas');
const stage = document.querySelector('#core-stage');
const legacyCanvas = document.querySelector('#core');
const overview = document.querySelector('#overview');
const modeLabel = document.querySelector('.core-mode');
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
const finePointer = matchMedia('(pointer: fine)');
if (legacyCanvas) legacyCanvas.hidden = true;
const staticFallback = () => {
  stage?.classList.remove('webgl-ready');
  if (canvas) { canvas.hidden = true; canvas.dataset.engine = 'static'; canvas.dataset.bloom = 'unavailable'; canvas.dataset.motion = 'paused'; }
  if (modeLabel) modeLabel.textContent = 'ORBITAL CORE';
};

if (canvas && stage) {
  try {
    const renderer = new THREE.WebGLRenderer({ canvas, alpha: false, antialias: false, powerPreference: 'high-performance' });
    renderer.setClearColor(0x0a2238, 1);
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.04;
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x0a2238);
    const camera = new THREE.PerspectiveCamera(44, 1, .1, 80);
    camera.position.z = 10;
    const atom = new THREE.Group();
    const field = new THREE.Group();
    scene.add(field, atom);
    let width = document.documentElement.clientWidth || innerWidth, height = innerHeight, mobile = width <= 700;
    let composer = null, bloomPass = null, outputPass = null;
    let raf = null, stillRaf = null, hidden = document.hidden, contextLost = false;
    let elapsed = 0, lastPaint = 0, lastRendered = 0, drawnFrames = 0, layoutDirty = true, orbitFitDirty = true, disposed = false;
    let pointerX = 0, pointerY = 0, smoothX = 0, smoothY = 0, scrollTarget = 0, smoothScroll = 0;
    let coreFade = 1, radiusPixels = 170, unitsPerPixel = .01;
    let stageCenterX = width * .58, stageCenterY = height * .4;
    const emissive = (r, g, b) => new THREE.Color().setRGB(r, g, b);
    const cyanHDR = emissive(.16, 2.8, 4.4);
    const iceHDR = emissive(3.6, 6.4, 7.5);
    const blueHDR = emissive(.14, .8, 4.8);
    const goldHDR = emissive(4.5, 1.1, .06);
    const atomMaterials = [];
    function remember(material, opacity = material.opacity ?? 1) {
      atomMaterials.push({ material, opacity });
      return material;
    }

    const glowCanvas = document.createElement('canvas');
    glowCanvas.width = glowCanvas.height = 128;
    const context = glowCanvas.getContext('2d');
    const gradient = context.createRadialGradient(64, 64, 0, 64, 64, 64);
    gradient.addColorStop(0, 'rgba(255,255,255,1)');
    gradient.addColorStop(.035, 'rgba(235,255,255,1)');
    gradient.addColorStop(.13, 'rgba(160,245,255,.8)');
    gradient.addColorStop(.32, 'rgba(55,180,255,.28)');
    gradient.addColorStop(.62, 'rgba(10,100,220,.055)');
    gradient.addColorStop(1, 'rgba(0,40,120,0)');
    context.fillStyle = gradient;
    context.fillRect(0, 0, 128, 128);
    const glowTexture = new THREE.CanvasTexture(glowCanvas);
    function sprite(color, scale, opacity = 1, parent = atom) {
      const material = remember(new THREE.SpriteMaterial({ map: glowTexture, color, opacity, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false }), opacity);
      const object = new THREE.Sprite(material);
      object.scale.setScalar(scale);
      parent.add(object);
      return object;
    }

    const shellUniforms = { time: { value: 0 }, fade: { value: 1 } };
    const shell = new THREE.Mesh(new THREE.IcosahedronGeometry(1, 3), new THREE.ShaderMaterial({
      uniforms: shellUniforms,
      vertexShader: `varying vec3 n; varying vec3 eye; varying vec3 p;
        void main(){vec4 mv=modelViewMatrix*vec4(position,1.0);n=normalize(normalMatrix*normal);eye=normalize(-mv.xyz);p=position;gl_Position=projectionMatrix*mv;}`,
      fragmentShader: `varying vec3 n; varying vec3 eye; varying vec3 p; uniform float time; uniform float fade;
        void main(){float rim=pow(1.0-abs(dot(normalize(n),normalize(eye))),.72);float scan=pow(max(0.0,1.0-abs(p.y-sin(time*.32)*.95)*13.0),2.0);vec3 energy=mix(vec3(.015,.32,.59),vec3(.055,.82,1.0),rim);float edge=smoothstep(.975,.999,rim);energy+=vec3(.015,2.4,4.2)*edge;gl_FragColor=vec4(energy*(1.0+scan*.12),(.16+rim*.36+scan*.04)*fade);}`,
      wireframe: true, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, toneMapped: false
    }));
    shell.rotation.z = -.12;
    shell.renderOrder = 3;
    atom.add(shell);
    // A dark nucleus masks the starfield and preserves the DOM name over its center.
    const nucleus = new THREE.Mesh(new THREE.SphereGeometry(.98, 40, 26), remember(new THREE.MeshBasicMaterial({ color: 0x031522, transparent: true, opacity: .95, depthWrite: true }), .95));
    nucleus.renderOrder = -1;
    atom.add(nucleus);
    const latitude = new THREE.Mesh(new THREE.SphereGeometry(1.012, 24, 14), remember(new THREE.MeshBasicMaterial({ color: emissive(.04, .62, .82), wireframe: true, opacity: .2, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, toneMapped: false }), .2));
    latitude.renderOrder = 4;
    atom.add(latitude);
    const innerLattice = new THREE.Mesh(new THREE.IcosahedronGeometry(.99, 2), remember(new THREE.MeshBasicMaterial({ color: emissive(.04, .34, .52), wireframe: true, opacity: .12, transparent: true, depthWrite: false, depthTest: false, blending: THREE.AdditiveBlending, toneMapped: false }), .12));
    innerLattice.rotation.set(.32, -.25, .16);
    innerLattice.renderOrder = 2;
    atom.add(innerLattice);
    const longitude = new THREE.Mesh(new THREE.SphereGeometry(1.015, 12, 18), remember(new THREE.MeshBasicMaterial({ color: emissive(.025, .4, .66), wireframe: true, opacity: .16, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, toneMapped: false }), .16));
    longitude.rotation.set(.6, .25, .7);
    longitude.renderOrder = 4;
    atom.add(longitude);

    const haloUniforms = { fade: { value: 1 }, time: { value: 0 } };
    const halo = new THREE.Mesh(new THREE.PlaneGeometry(5.7, 5.7), new THREE.ShaderMaterial({
      uniforms: haloUniforms,
      vertexShader: `varying vec2 vUv;void main(){vUv=uv;gl_Position=projectionMatrix*modelViewMatrix*vec4(position,1.0);}`,
      fragmentShader: `varying vec2 vUv;uniform float fade;uniform float time;
        void main(){vec2 p=vUv-.5;float r=length(p);float haze=exp(-pow((r-.19)*22.0,2.0))*.025;float edge=exp(-pow((r-.182)*225.0,2.0))*.28;float sweep=.8+.2*sin(atan(p.y,p.x)*6.0-time*.35);gl_FragColor=vec4(vec3(.015,.55,.9)*(haze+edge)*sweep,(haze+edge*.5)*fade);}`,
      transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, depthTest: false, toneMapped: false
    }));
    halo.position.z = -.06;
    halo.renderOrder = 0;
    atom.add(halo);

    const shellNodes = new THREE.Group();
    const nodePositions = [];
    const nodeCount = mobile ? 52 : 110;
    for (let i = 0; i < nodeCount; i++) {
      const y = 1 - (i / Math.max(1, nodeCount - 1)) * 2;
      const r = Math.sqrt(1 - y * y), a = i * 2.399963;
      nodePositions.push(Math.cos(a) * r * 1.025, y * 1.025, Math.sin(a) * r * 1.025);
    }
    const nodeGeometry = new THREE.BufferGeometry();
    nodeGeometry.setAttribute('position', new THREE.Float32BufferAttribute(nodePositions, 3));
    const starsOnShell = new THREE.Points(nodeGeometry, remember(new THREE.PointsMaterial({ map: glowTexture, color: emissive(.15, .9, 1), size: .027, opacity: .65, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false }), .65));
    shellNodes.add(starsOnShell);
    shellNodes.renderOrder = 5;
    atom.add(shellNodes);

    const orbiters = [];
    const orbitEnvelope = new THREE.Group();
    atom.add(orbitEnvelope);
    function orbit(radius, tilt, turn, spin, speed, phase, color) {
      const group = new THREE.Group();
      const points = [];
      for (let i = 0; i <= 180; i++) {
        const a = i / 180 * Math.PI * 2;
        points.push(new THREE.Vector3(Math.cos(a) * radius, Math.sin(a) * radius, 0));
      }
      // Fine base rails stay below the bloom threshold; only short moving
      // energy segments and electron hotspots contribute HDR glare.
      const railCurve = new THREE.CatmullRomCurve3(points.slice(0, -1), true, 'centripetal');
      const railColor = color === goldHDR ? emissive(1, .36, .025) : color === blueHDR ? emissive(.035, .3, 1) : emissive(.025, .78, 1);
      const rail = new THREE.Mesh(new THREE.TubeGeometry(railCurve, mobile ? 144 : 216, mobile ? .006 : .0045, 5, true), remember(new THREE.MeshBasicMaterial({ color: railColor, transparent: true, opacity: .68, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false }), .68));
      group.add(rail);
      const energyPoints = [];
      for (let j = 0; j <= 12; j++) {
        const a = -Math.sign(speed) * .075 * (1 - j / 12);
        energyPoints.push(new THREE.Vector3(Math.cos(a) * radius, Math.sin(a) * radius, 0));
      }
      const energyCurve = new THREE.CatmullRomCurve3(energyPoints);
      const energySegment = new THREE.Mesh(new THREE.TubeGeometry(energyCurve, 20, .0055, 5, false), remember(new THREE.MeshBasicMaterial({ color: color.clone().multiplyScalar(color === blueHDR ? 1.7 : 1.15), opacity: .9, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false }), .9));
      group.add(energySegment);
      group.rotation.set(tilt, turn, spin);
      orbitEnvelope.add(group);
      const electron = new THREE.Mesh(new THREE.SphereGeometry(.025, 10, 8), remember(new THREE.MeshBasicMaterial({ color: iceHDR, transparent: true, blending: THREE.AdditiveBlending, toneMapped: false }), 1));
      const aura = sprite(color, .46, .58, group);
      const hotSpot = sprite(iceHDR, .19, 1, group);
      const streak = sprite(color, 1, .5, group);
      streak.scale.set(1.12, .028, 1);
      group.add(electron);
      const trail = [];
      for (let j = 0; j < 13; j++) {
        const dust = sprite(color, .13 - j * .0065, .38 - j * .025, group);
        trail.push(dust);
      }
      orbiters.push({ group, rail, energySegment, electron, aura, hotSpot, streak, trail, radius, speed, phase });
    }
    orbit(2.6, 1.16, .08, -.16, .25, .1, cyanHDR);
    orbit(2.93, 1.34, -.18, .18, -.19, 2.3, blueHDR);
    orbit(2.13, .6, .56, -.48, .16, 4.8, goldHDR);
    const segmented = new THREE.Group();
    for (let i = 0; i < 9; i++) {
      const points = [];
      for (let j = 0; j <= 16; j++) {
        const a = i / 9 * Math.PI * 2 + j / 16 * .35;
        points.push(new THREE.Vector3(Math.cos(a) * 1.19, Math.sin(a) * 1.19, .04));
      }
      segmented.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(points), remember(new THREE.LineBasicMaterial({ color: emissive(.06, .8, 1), opacity: i % 3 ? .35 : .6, transparent: true, blending: THREE.AdditiveBlending, toneMapped: false }), i % 3 ? .35 : .6)));
    }
    const tickPositions = [];
    for (let i = 0; i < 96; i++) {
      const a = i / 96 * Math.PI * 2, inner = 1.245, outer = i % 8 ? 1.27 : 1.32;
      tickPositions.push(Math.cos(a) * inner, Math.sin(a) * inner, .03, Math.cos(a) * outer, Math.sin(a) * outer, .03);
    }
    const tickGeometry = new THREE.BufferGeometry();
    tickGeometry.setAttribute('position', new THREE.Float32BufferAttribute(tickPositions, 3));
    segmented.add(new THREE.LineSegments(tickGeometry, remember(new THREE.LineBasicMaterial({ color: emissive(.055, .72, .95), opacity: .46, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false }), .46)));
    [1.12, 1.345].forEach((radius, index) => {
      const points = [];
      for (let i = 0; i <= 160; i++) { const a = i / 160 * Math.PI * 2; points.push(new THREE.Vector3(Math.cos(a) * radius, Math.sin(a) * radius, .025)); }
      segmented.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(points), remember(new THREE.LineBasicMaterial({ color: emissive(.04, .48, .76), opacity: index ? .35 : .23, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false }), index ? .35 : .23)));
    });
    atom.add(segmented);
    const ghostColors = [cyanHDR, blueHDR, cyanHDR];
    const ghosts = ghostColors.map((color, i) => {
      const ghost = sprite(color, .3 + i * .24, .025 + i * .008);
      ghost.position.set(-1.9 + i * .7, .4 - i * .2, .2);
      return ghost;
    });

    const floor = new THREE.Group();
    floor.position.y = -1.2;
    floor.rotation.x = Math.PI * .44;
    atom.add(floor);
    for (let i = 0; i < 9; i++) {
      const radius = .8 + i * .13, points = [];
      for (let j = 0; j <= 128; j++) {
        const a = j / 128 * Math.PI * 2;
        points.push(new THREE.Vector3(Math.cos(a) * radius, Math.sin(a) * radius, 0));
      }
      floor.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(points), remember(new THREE.LineBasicMaterial({ color: cyanHDR, transparent: true, opacity: .075 + i * .007, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false }), .075 + i * .007)));
    }

    // The background never shares the DOM text plane, so bloom cannot soften labels.
    const particleLayers = [];
    [mobile ? 50 : 170, mobile ? 28 : 100, mobile ? 12 : 60].forEach((count, layer) => {
      const positions = new Float32Array(count * 3);
      for (let i = 0; i < count; i++) {
        positions[i * 3] = (Math.random() - .5) * 42;
        positions[i * 3 + 1] = (Math.random() - .5) * 24;
        positions[i * 3 + 2] = -5 + layer * 1.5 + (Math.random() - .5) * 2;
      }
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
      const particles = new THREE.Points(geometry, new THREE.PointsMaterial({ map: glowTexture, color: new THREE.Color(.1, .53, .85), size: .035 + layer * .025, opacity: .38 - layer * .04, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false }));
      field.add(particles);
      particleLayers.push(particles);
    });
    const anchorPoints = Array.from({ length: mobile ? 8 : 24 }, () => new THREE.Vector3((Math.random() - .5) * 30, (Math.random() - .5) * 16, -3 - Math.random() * 3));
    const networkPositions = [];
    anchorPoints.forEach((p, i) => anchorPoints.slice(i + 1).forEach(q => { if (p.distanceTo(q) < 4.6) networkPositions.push(p.x, p.y, p.z, q.x, q.y, q.z); }));
    const networkGeometry = new THREE.BufferGeometry();
    networkGeometry.setAttribute('position', new THREE.Float32BufferAttribute(networkPositions, 3));
    const network = new THREE.LineSegments(networkGeometry, new THREE.LineBasicMaterial({ color: 0x1c8eb4, opacity: .09, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending }));
    field.add(network);

    function disposeComposer() {
      if (!composer) return;
      bloomPass?.dispose();
      outputPass?.dispose();
      composer.dispose();
      composer = bloomPass = outputPass = null;
    }
    function setupBloom() {
      disposeComposer();
      const gl = renderer.getContext();
      if (!renderer.extensions.has('EXT_color_buffer_float')) { canvas.dataset.bloom = 'direct-fallback'; return; }
      try {
        const target = new THREE.WebGLRenderTarget(1, 1, { type: THREE.HalfFloatType, format: THREE.RGBAFormat, depthBuffer: true });
        renderer.setRenderTarget(target);
        const complete = gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE;
        renderer.setRenderTarget(null);
        if (!complete) { target.dispose(); canvas.dataset.bloom = 'direct-fallback'; return; }
        composer = new EffectComposer(renderer, target);
        composer.addPass(new RenderPass(scene, camera));
        bloomPass = new UnrealBloomPass(new THREE.Vector2(width, height), 1.4, .16, 1);
        bloomPass.compositeMaterial.uniforms.bloomFactors.value = [1, .58, .2, .05, 0];
        // Large blur mips otherwise wash out fine mechanics even at a low
        // radius. Keep the first two bright, then suppress wide haze.
        [1, .7, .24, .04, .004].forEach((weight, i) => bloomPass.bloomTintColors[i].setScalar(weight));
        const nativeBloomSize = bloomPass.setSize.bind(bloomPass);
        bloomPass.setSize = (w, h) => nativeBloomSize(Math.max(1, Math.round(w * (mobile ? .55 : .75))), Math.max(1, Math.round(h * (mobile ? .55 : .75))));
        composer.addPass(bloomPass);
        outputPass = new OutputPass();
        composer.addPass(outputPass);
        composer.setPixelRatio(renderer.getPixelRatio());
        composer.setSize(width, height);
        canvas.dataset.bloom = 'hdr-unreal';
      } catch (error) {
        renderer.setRenderTarget(null);
        disposeComposer();
        canvas.dataset.bloom = 'direct-fallback';
        console.info('HDR bloom unavailable; direct 3D rendering retained.');
      }
    }
    const motionEnabled = () => typeof window.portfolioMotion === 'boolean' ? window.portfolioMotion : !reducedMotion.matches;
    const allowed = () => motionEnabled() && !hidden && !contextLost && !disposed;
    function measure() {
      const r = stage.getBoundingClientRect();
      const area = overview?.getBoundingClientRect() ?? r;
      stageCenterX = r.left - area.left + r.width / 2;
      stageCenterY = r.top - area.top + r.height * .44;
      // Keep the full orbital envelope + glow inside a ten-percent mobile gutter.
      radiusPixels = mobile ? Math.min(93, width * .245, r.height * .32) : Math.min(180, r.height * .35);
      coreFade = 1;
      atom.visible = coreFade > .005;
      scrollTarget = Math.min(scrollY / Math.max(height, 1), 8);
      orbitFitDirty = true;
      layoutDirty = false;
    }
    function fitOrbitEnvelope() {
      // Fit the projected orbital container independently on each axis. The
      // nucleus keeps its full diameter; desktop rails retain their span when
      // there is room and asymmetric tablet anchors cannot clip an electron.
      orbitEnvelope.scale.set(1, 1, 1);
      const edge = Math.max(12, Math.min(26, height * .026));
      const glow = Math.max(18, radiusPixels * .3);
      const upperRoom = Math.max(35, stageCenterY - edge - glow);
      const lowerRoom = Math.max(35, height - stageCenterY - edge - glow);
      const leftRoom = Math.max(35, stageCenterX - edge - glow);
      const rightRoom = Math.max(35, width - stageCenterX - edge - glow);
      const point = new THREE.Vector3();
      camera.updateMatrixWorld();
      for (let pass = 0; pass < 4; pass++) {
        atom.updateMatrixWorld(true);
        let upper = 0, lower = 0, left = 0, right = 0;
        for (const orbit of orbiters) {
          for (let i = 0; i < 64; i++) {
            const angle = i / 64 * Math.PI * 2;
            point.set(Math.cos(angle) * orbit.radius, Math.sin(angle) * orbit.radius, 0).applyMatrix4(orbit.group.matrixWorld).project(camera);
            const x = (point.x + 1) * width / 2;
            const y = (1 - point.y) * height / 2;
            upper = Math.max(upper, stageCenterY - y);
            lower = Math.max(lower, y - stageCenterY);
            left = Math.max(left, stageCenterX - x);
            right = Math.max(right, x - stageCenterX);
          }
        }
        const correctionY = Math.min(1, upperRoom / Math.max(1, upper), lowerRoom / Math.max(1, lower));
        const correctionX = Math.min(1, leftRoom / Math.max(1, left), rightRoom / Math.max(1, right));
        if (correctionY >= .995 && correctionX >= .995) break;
        if (correctionY < .995) orbitEnvelope.scale.y = Math.max(.18, orbitEnvelope.scale.y * correctionY * .98);
        if (correctionX < .995) orbitEnvelope.scale.x = Math.max(.18, orbitEnvelope.scale.x * correctionX * .98);
      }
      orbitFitDirty = false;
    }
    function layoutAtom() {
      unitsPerPixel = 2 * camera.position.z * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) / height;
      atom.position.set((stageCenterX - width / 2) * unitsPerPixel, (height / 2 - stageCenterY) * unitsPerPixel, 0);
      atom.scale.setScalar(radiusPixels * unitsPerPixel);
      const orbitCompression = mobile ? .42 : width < 1100 ? .73 : 1;
      orbiters.forEach(o => { o.group.scale.set(orbitCompression, mobile ? .8 : 1, orbitCompression); });
      if (orbitFitDirty) fitOrbitEnvelope();
      halo.scale.setScalar(mobile ? .86 : 1);
      ghosts.forEach(ghost => { ghost.visible = !mobile; });
      shellUniforms.fade.value = haloUniforms.fade.value = coreFade;
      atomMaterials.forEach(({ material, opacity }) => { material.opacity = opacity * coreFade; });
    }
    function pose(delta = 0) {
      if (layoutDirty) measure();
      const ease = Math.min(1, delta * 3 || 1);
      smoothX += (pointerX - smoothX) * ease;
      smoothY += (pointerY - smoothY) * ease;
      smoothScroll += (scrollTarget - smoothScroll) * ease;
      if (atom.visible) {
        atom.rotation.y = smoothX * .1;
        atom.rotation.x = -smoothY * .055;
        layoutAtom();
        shellUniforms.time.value = haloUniforms.time.value = elapsed;
        shell.rotation.y = elapsed * .045;
        latitude.rotation.y = -elapsed * .023;
        longitude.rotation.y = elapsed * .018;
        innerLattice.rotation.y = -elapsed * .028;
        shellNodes.rotation.copy(shell.rotation);
        segmented.rotation.z = elapsed * .06;
        orbiters.forEach((o, i) => {
          const a = elapsed * o.speed + o.phase;
          const x = Math.cos(a) * o.radius, y = Math.sin(a) * o.radius;
          o.energySegment.rotation.z = a;
          [o.electron, o.aura, o.hotSpot, o.streak].forEach(node => node.position.set(x, y, .025));
          o.trail.forEach((node, j) => {
            const b = a - Math.sign(o.speed) * (j + 1) * .014;
            node.position.set(Math.cos(b) * o.radius, Math.sin(b) * o.radius, .018);
          });
          const pulse = .96 + Math.sin(elapsed * 1.2 + i) * .07;
          o.aura.scale.setScalar(.46 * pulse);
        });
        ghosts.forEach((ghost, i) => { ghost.position.x = -1.9 + i * .7 + Math.sin(elapsed * .1) * .05; });
      }
      particleLayers.forEach((layer, i) => {
        layer.rotation.z = Math.sin(elapsed * .012 + i) * .02;
        layer.position.x = Math.sin(elapsed * .018 + i) * .08 - smoothX * (.1 + i * .1);
        layer.position.y = smoothScroll * (.08 + i * .12) - smoothY * .07;
      });
      network.position.set(-smoothX * .12, smoothScroll * .14, 0);
    }
    function draw() {
      if (contextLost || disposed) return;
      if (composer) composer.render(); else renderer.render(scene, camera);
      canvas.dataset.frames = String(++drawnFrames);
      if (legacyCanvas) { legacyCanvas.dataset.frames = String(drawnFrames); legacyCanvas.dataset.engine = 'threejs-shared'; legacyCanvas.dataset.motion = canvas.dataset.motion; }
    }
    function size() {
      const bounds = stage.getBoundingClientRect();
      width = Math.max(1, Math.round(bounds.width));
      height = Math.max(1, Math.round(bounds.height));
      mobile = width <= 700;
      renderer.setPixelRatio(Math.min(devicePixelRatio, 1));
      renderer.setSize(width, height, false);
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
      composer?.setPixelRatio(renderer.getPixelRatio());
      composer?.setSize(width, height);
      layoutDirty = true;
      requestStill();
    }
    function requestStill() {
      if (allowed() || hidden || contextLost || disposed || stillRaf !== null) return;
      stillRaf = requestAnimationFrame(() => { stillRaf = null; pose(); draw(); });
    }
    function frame(now) {
      raf = null;
      if (!allowed()) return;
      const interval = 1000 / 30;
      if (now - lastPaint >= interval) {
        const delta = Math.min((now - lastRendered) / 1000, .08);
        lastRendered = now;
        lastPaint = now - ((now - lastPaint) % interval);
        elapsed += delta;
        pose(delta);
        draw();
      }
      raf = requestAnimationFrame(frame);
    }
    function resume() {
      if (raf !== null) cancelAnimationFrame(raf);
      if (stillRaf !== null) cancelAnimationFrame(stillRaf);
      raf = stillRaf = null;
      lastPaint = lastRendered = performance.now();
      canvas.dataset.motion = allowed() ? 'playing' : 'paused';
      if (legacyCanvas) legacyCanvas.dataset.motion = canvas.dataset.motion;
      if (!hidden && !contextLost) { pose(); draw(); }
      if (allowed()) raf = requestAnimationFrame(frame);
    }
    const markLayout = () => { layoutDirty = true; requestStill(); };
    window.addEventListener('resize', size, { passive: true });
    window.addEventListener('scroll', markLayout, { passive: true });
    const resizeObserver = new ResizeObserver(size);
    resizeObserver.observe(stage);
    if (overview) resizeObserver.observe(overview);
    window.addEventListener('pointermove', e => {
      if (!finePointer.matches || !motionEnabled()) return;
      pointerX = (e.clientX - stage.getBoundingClientRect().left) / width - .5;
      pointerY = (e.clientY - stage.getBoundingClientRect().top) / height - .5;
    }, { passive: true });
    document.addEventListener('pointerleave', () => { pointerX = pointerY = 0; });
    window.addEventListener('portfolio-motion', resume);
    reducedMotion.addEventListener('change', resume);
    document.addEventListener('visibilitychange', () => { hidden = document.hidden; resume(); });
    canvas.addEventListener('webglcontextlost', e => {
      e.preventDefault(); contextLost = true;
      if (raf !== null) cancelAnimationFrame(raf);
      if (stillRaf !== null) cancelAnimationFrame(stillRaf);
      raf = stillRaf = null;
      staticFallback();
    });
    canvas.addEventListener('webglcontextrestored', () => {
      contextLost = false; canvas.hidden = false;
      setupBloom(); size(); stage.classList.add('webgl-ready');
      canvas.dataset.engine = 'threejs'; if (modeLabel) modeLabel.textContent = 'HDR ATOMIC CORE'; resume();
    });
    window.addEventListener('pagehide', () => { disposed = true; if (raf !== null) cancelAnimationFrame(raf); if (stillRaf !== null) cancelAnimationFrame(stillRaf); raf = stillRaf = null; });
    window.addEventListener('pageshow', () => { disposed = false; layoutDirty = true; resume(); });
    size();
    setupBloom();
    measure(); pose();
    canvas.hidden = false;
    stage.classList.add('webgl-ready');
    canvas.dataset.engine = 'threejs';
    canvas.dataset.revision = THREE.REVISION;
    if (modeLabel) modeLabel.textContent = composer ? 'HDR ATOMIC CORE' : 'INTERACTIVE 3D';
    resume();
  } catch (error) {
    staticFallback();
    console.info('The static orbital core is displayed because WebGL is unavailable.');
  }
}
