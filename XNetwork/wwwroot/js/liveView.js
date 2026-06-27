const scenes = new WeakMap();

let threeModulePromise;

export async function initialize(element, dotNetReference) {
    if (!element) {
        return;
    }

    dispose(element);

    const state = {
        element,
        dotNetReference,
        renderer: null,
        scene: null,
        camera: null,
        raycaster: null,
        pointer: null,
        tunnelCore: null,
        serverCore: null,
        fieldGroup: null,
        pathGroup: null,
        beamGroup: null,
        packetGroup: null,
        nodeMeshes: new Map(),
        packets: [],
        data: null,
        animationId: 0,
        resizeObserver: null,
        fallbackCanvas: null,
        fallbackContext: null,
        fallbackAnimationId: 0,
        pointerDownHandler: null,
        lastFrameTime: performance.now(),
        glowTexture: null
    };

    scenes.set(element, state);

    try {
        const THREE = await loadThree();
        setupThreeScene(state, THREE);
    } catch (error) {
        console.warn('Live view: using canvas fallback because Three.js failed to load.', error);
        setupFallbackCanvas(state);
    }
}

export function update(element, payload) {
    const state = scenes.get(element);
    if (!state) {
        return;
    }

    state.data = payload || {};

    if (state.scene) {
        updateThreeScene(state);
        return;
    }

    drawFallback(state);
}

export function dispose(element) {
    const state = scenes.get(element);
    if (!state) {
        return;
    }

    if (state.animationId) {
        cancelAnimationFrame(state.animationId);
    }

    if (state.fallbackAnimationId) {
        cancelAnimationFrame(state.fallbackAnimationId);
    }

    state.resizeObserver?.disconnect();

    if (state.pointerDownHandler) {
        state.element.removeEventListener('pointerdown', state.pointerDownHandler);
    }

    if (state.renderer) {
        disposeObjectTree(state.scene);
        state.glowTexture?.dispose?.();
        state.renderer.dispose();
        state.renderer.domElement.remove();
    }

    state.fallbackCanvas?.remove();
    scenes.delete(element);
}

async function loadThree() {
    threeModulePromise ??= import('https://cdn.jsdelivr.net/npm/three@0.165.0/build/three.module.js');
    return threeModulePromise;
}

function setupThreeScene(state, THREE) {
    const { element } = state;

    const renderer = new THREE.WebGLRenderer({
        antialias: true,
        alpha: true,
        powerPreference: 'high-performance'
    });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    element.replaceChildren(renderer.domElement);

    const scene = new THREE.Scene();
    scene.fog = new THREE.FogExp2(0x07090d, 0.035);

    const camera = new THREE.PerspectiveCamera(46, 1, 0.1, 100);
    camera.position.set(0, 2.6, 9.6);
    camera.lookAt(0, 0, 0);

    const fieldGroup = new THREE.Group();
    const pathGroup = new THREE.Group();
    const beamGroup = new THREE.Group();
    const packetGroup = new THREE.Group();
    scene.add(fieldGroup, beamGroup, packetGroup, pathGroup);

    const ambient = new THREE.AmbientLight(0x5f7f9f, 0.45);
    const coreLight = new THREE.PointLight(0x22d3ee, 50, 10);
    coreLight.position.set(0, 0.2, 0);
    const rimLight = new THREE.PointLight(0xf472b6, 28, 12);
    rimLight.position.set(-4.5, 1.8, -1.5);
    scene.add(ambient, coreLight, rimLight);

    const glowTexture = createGlowTexture(THREE);
    const tunnelCore = createEnergyCore(THREE, glowTexture);
    const serverCore = createEnergyGate(THREE, glowTexture);
    serverCore.position.set(4.35, 0.05, -1.15);
    scene.add(tunnelCore, serverCore);

    const field = createEnergyField(THREE);
    fieldGroup.add(field);

    state.THREE = THREE;
    state.renderer = renderer;
    state.scene = scene;
    state.camera = camera;
    state.raycaster = new THREE.Raycaster();
    state.pointer = new THREE.Vector2();
    state.fieldGroup = fieldGroup;
    state.pathGroup = pathGroup;
    state.beamGroup = beamGroup;
    state.packetGroup = packetGroup;
    state.tunnelCore = tunnelCore;
    state.serverCore = serverCore;
    state.glowTexture = glowTexture;

    state.pointerDownHandler = event => handlePointerDown(state, event);
    element.addEventListener('pointerdown', state.pointerDownHandler);

    const resize = () => resizeRenderer(state);
    state.resizeObserver = new ResizeObserver(resize);
    state.resizeObserver.observe(element);
    resize();

    animateThree(state);
}

function resizeRenderer(state) {
    const rect = state.element.getBoundingClientRect();
    const width = Math.max(1, Math.floor(rect.width));
    const height = Math.max(1, Math.floor(rect.height));

    state.renderer.setSize(width, height, false);
    state.camera.aspect = width / height;
    state.camera.updateProjectionMatrix();
}

function updateThreeScene(state) {
    const THREE = state.THREE;
    const paths = Array.isArray(state.data?.paths) ? state.data.paths : [];
    const selectedPathId = Number(state.data?.selectedPathId);

    disposeGroupChildren(state.pathGroup);
    disposeGroupChildren(state.beamGroup);
    disposeGroupChildren(state.packetGroup);
    state.nodeMeshes.clear();
    state.packets = [];

    const materialCache = new Map();
    const width = state.element.getBoundingClientRect().width;
    const compact = width < 560;
    const radiusX = compact ? 3.45 : 4.75;
    const radiusZ = compact ? 1.8 : 2.45;
    const count = Math.max(1, paths.length);

    paths.forEach((path, index) => {
        const position = getEmitterPosition(index, count, radiusX, radiusZ);
        const color = getPathColor(path);
        const selected = Number(path.id) === selectedPathId;
        const active = Boolean(path.active);
        const node = createPathEmitter(THREE, state.glowTexture, materialCache, path, color, selected);
        node.position.copy(position);
        node.userData.pathId = Number(path.id);
        state.pathGroup.add(node);
        state.nodeMeshes.set(Number(path.id), node);

        const target = active ? state.tunnelCore.position : state.serverCore.position;
        const lift = path.anchor ? 0.85 : active ? 0.62 : 0.35;
        const points = makeEnergyCurvePoints(position, target, lift, index);
        addEnergyBeam(state, materialCache, points, color, path);

        if (active) {
            addEnergyPackets(state, materialCache, points, color, path);
        }
    });

    const healthColor = getHealthColor(state.data);
    setEnergyCoreColor(state.tunnelCore, healthColor, state.data?.recovery);
}

function getEmitterPosition(index, count, radiusX, radiusZ) {
    if (count === 1) {
        return new THREE.Vector3(-radiusX, -0.15, 0.2);
    }

    const normalized = index / Math.max(1, count - 1);
    const angle = Math.PI * 0.73 + normalized * Math.PI * 0.86;
    const y = -0.42 + Math.sin(normalized * Math.PI) * 0.72;
    return new THREE.Vector3(
        Math.cos(angle) * radiusX,
        y,
        Math.sin(angle) * radiusZ - 0.25
    );
}

function createEnergyCore(THREE, glowTexture) {
    const core = new THREE.Group();
    core.name = 'XBond energy core';

    const glow = createGlowSprite(THREE, glowTexture, 0x22c55e, 1.35, 3.45);
    glow.userData.role = 'core-glow';
    core.add(glow);

    const rings = [];
    const ringSpecs = [
        { radius: 0.58, tube: 0.012, color: 0x9fffe3, opacity: 0.85, rotation: [Math.PI / 2, 0, 0] },
        { radius: 0.95, tube: 0.014, color: 0x22d3ee, opacity: 0.58, rotation: [1.1, 0.35, 0.12] },
        { radius: 1.35, tube: 0.011, color: 0x60a5fa, opacity: 0.36, rotation: [0.75, -0.58, 0.2] },
        { radius: 1.75, tube: 0.008, color: 0xf472b6, opacity: 0.22, rotation: [1.35, 0.82, -0.15] }
    ];

    ringSpecs.forEach((spec, index) => {
        const ring = new THREE.Mesh(
            new THREE.TorusGeometry(spec.radius, spec.tube, 8, 160),
            new THREE.MeshBasicMaterial({
                color: spec.color,
                transparent: true,
                opacity: spec.opacity,
                blending: THREE.AdditiveBlending,
                depthWrite: false
            })
        );
        ring.rotation.set(...spec.rotation);
        ring.userData.spin = index % 2 === 0 ? 1 : -1;
        ring.userData.baseOpacity = spec.opacity;
        rings.push(ring);
        core.add(ring);
    });

    const pulse = new THREE.Mesh(
        new THREE.RingGeometry(0.18, 0.22, 96),
        new THREE.MeshBasicMaterial({
            color: 0xffffff,
            transparent: true,
            opacity: 0.45,
            blending: THREE.AdditiveBlending,
            side: THREE.DoubleSide,
            depthWrite: false
        })
    );
    pulse.rotation.x = Math.PI / 2;
    pulse.userData.role = 'pulse';
    core.add(pulse);

    core.userData.glow = glow;
    core.userData.rings = rings;
    core.userData.pulse = pulse;
    return core;
}

function createEnergyGate(THREE, glowTexture) {
    const gate = new THREE.Group();
    gate.name = 'XBond server gate';

    const glow = createGlowSprite(THREE, glowTexture, 0x3794ff, 0.52, 1.65);
    gate.add(glow);

    for (let i = 0; i < 3; i += 1) {
        const ring = new THREE.Mesh(
            new THREE.TorusGeometry(0.34 + i * 0.16, 0.009, 8, 96),
            new THREE.MeshBasicMaterial({
                color: i === 0 ? 0xb7e7ff : 0x3794ff,
                transparent: true,
                opacity: 0.55 - i * 0.12,
                blending: THREE.AdditiveBlending,
                depthWrite: false
            })
        );
        ring.rotation.y = Math.PI / 2;
        ring.userData.spin = i % 2 === 0 ? 1 : -1;
        gate.add(ring);
    }

    return gate;
}

function createEnergyField(THREE) {
    const count = 180;
    const positions = new Float32Array(count * 3);
    const colors = new Float32Array(count * 3);
    const color = new THREE.Color();

    for (let i = 0; i < count; i += 1) {
        const radius = 2.8 + Math.random() * 5.8;
        const angle = Math.random() * Math.PI * 2;
        positions[i * 3] = Math.cos(angle) * radius;
        positions[i * 3 + 1] = -1.7 + Math.random() * 3.4;
        positions[i * 3 + 2] = Math.sin(angle) * radius * 0.55 - 1.2;

        color.setHex(i % 5 === 0 ? 0xf472b6 : i % 3 === 0 ? 0x22c55e : 0x22d3ee);
        colors[i * 3] = color.r;
        colors[i * 3 + 1] = color.g;
        colors[i * 3 + 2] = color.b;
    }

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));

    return new THREE.Points(
        geometry,
        new THREE.PointsMaterial({
            size: 0.018,
            vertexColors: true,
            transparent: true,
            opacity: 0.55,
            blending: THREE.AdditiveBlending,
            depthWrite: false
        })
    );
}

function createPathEmitter(THREE, glowTexture, cache, path, color, selected) {
    const node = new THREE.Group();
    const active = Boolean(path.active);
    const scale = selected ? 1.16 : 1;
    const opacity = active ? 0.92 : path.up ? 0.55 : 0.35;

    const glow = createGlowSprite(THREE, glowTexture, color, opacity, (active ? 1.05 : 0.72) * scale);
    node.add(glow);

    const outer = new THREE.Mesh(
        new THREE.TorusGeometry(0.34 * scale, 0.008, 8, 92),
        getBasicMaterial(cache, THREE, color, active ? 0.78 : 0.36)
    );
    outer.rotation.x = Math.PI / 2.2;
    outer.userData.spin = active ? 1 : 0.5;
    node.add(outer);

    const inner = new THREE.Mesh(
        new THREE.TorusGeometry(0.18 * scale, 0.011, 8, 80),
        getBasicMaterial(cache, THREE, color, active ? 0.95 : 0.45)
    );
    inner.rotation.y = Math.PI / 2;
    inner.userData.spin = active ? -1.35 : -0.65;
    node.add(inner);

    if (selected) {
        const selectedHalo = new THREE.Mesh(
            new THREE.TorusGeometry(0.48, 0.012, 8, 112),
            getBasicMaterial(cache, THREE, 0x3794ff, 0.92)
        );
        selectedHalo.rotation.x = Math.PI / 2;
        selectedHalo.userData.spin = 1.4;
        node.add(selectedHalo);
    }

    const hitTarget = new THREE.Mesh(
        new THREE.SphereGeometry(0.62, 16, 12),
        new THREE.MeshBasicMaterial({
            color,
            transparent: true,
            opacity: 0,
            depthWrite: false
        })
    );
    hitTarget.name = path.name || path.iface || 'adapter';
    node.add(hitTarget);

    node.userData.rings = [outer, inner];
    return node;
}

function addEnergyBeam(state, cache, points, color, path) {
    const THREE = state.THREE;
    const active = Boolean(path.active);
    const anchor = Boolean(path.anchor);
    const standbyOpacity = path.up ? 0.15 : 0.06;
    const baseOpacity = active ? (anchor ? 0.62 : 0.5) : standbyOpacity;
    const curve = new THREE.CatmullRomCurve3(points);

    if (active) {
        const halo = new THREE.Mesh(
            new THREE.TubeGeometry(curve, 72, anchor ? 0.045 : 0.036, 8, false),
            getEnergyMaterial(cache, THREE, color, 0.105)
        );
        state.beamGroup.add(halo);
    }

    const beam = new THREE.Mesh(
        new THREE.TubeGeometry(curve, 88, active ? 0.014 : 0.008, 7, false),
        getEnergyMaterial(cache, THREE, color, baseOpacity)
    );
    state.beamGroup.add(beam);

    const line = new THREE.Line(
        new THREE.BufferGeometry().setFromPoints(points),
        getLineMaterial(cache, THREE, color, active ? 0.86 : 0.22)
    );
    state.beamGroup.add(line);
}

function addEnergyPackets(state, cache, points, color, path) {
    const THREE = state.THREE;
    const throughput = Math.min(5, Number(path.down || 0) + Number(path.upMbps || 0));
    const packetCount = Math.max(3, Math.min(8, Math.ceil(throughput) + 3));

    for (let packetIndex = 0; packetIndex < packetCount; packetIndex += 1) {
        const packet = createGlowSprite(
            THREE,
            state.glowTexture,
            color,
            path.anchor ? 0.95 : 0.82,
            path.anchor ? 0.19 : 0.15
        );
        packet.userData = {
            points,
            offset: packetIndex / packetCount,
            speed: path.anchor ? 0.34 : 0.27,
            reverse: packetIndex % 3 === 0
        };
        state.packetGroup.add(packet);
        state.packets.push(packet);
    }
}

function makeEnergyCurvePoints(from, to, lift, seed) {
    const points = [];
    for (let i = 0; i <= 56; i += 1) {
        const t = i / 56;
        const point = from.clone().lerp(to, t);
        const wave = Math.sin(t * Math.PI * 2.2 + seed * 0.75) * 0.12 * Math.sin(t * Math.PI);
        point.y += Math.sin(t * Math.PI) * lift + wave;
        point.x += Math.sin(t * Math.PI * 1.6 + seed) * 0.08 * Math.sin(t * Math.PI);
        point.z += Math.cos(t * Math.PI * 1.4 + seed) * 0.06 * Math.sin(t * Math.PI);
        points.push(point);
    }

    return points;
}

function createGlowTexture(THREE) {
    const size = 128;
    const canvas = document.createElement('canvas');
    canvas.width = size;
    canvas.height = size;
    const ctx = canvas.getContext('2d');
    const gradient = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
    gradient.addColorStop(0, 'rgba(255,255,255,1)');
    gradient.addColorStop(0.25, 'rgba(255,255,255,0.72)');
    gradient.addColorStop(0.52, 'rgba(255,255,255,0.22)');
    gradient.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, size, size);

    const texture = new THREE.CanvasTexture(canvas);
    texture.colorSpace = THREE.SRGBColorSpace;
    return texture;
}

function createGlowSprite(THREE, glowTexture, color, opacity, size) {
    const material = new THREE.SpriteMaterial({
        map: glowTexture,
        color,
        transparent: true,
        opacity,
        blending: THREE.AdditiveBlending,
        depthWrite: false
    });
    const sprite = new THREE.Sprite(material);
    sprite.scale.set(size, size, 1);
    return sprite;
}

function setEnergyCoreColor(core, color, recovery) {
    const glow = core.userData.glow;
    if (glow?.material?.color) {
        glow.material.color.setHex(color);
        glow.material.opacity = recovery ? 1 : 0.88;
    }

    core.userData.rings?.forEach((ring, index) => {
        ring.material.color.setHex(index === 3 && recovery ? 0xfb923c : color);
        ring.material.opacity = Math.min(0.92, ring.userData.baseOpacity + (recovery ? 0.18 : 0));
    });

    if (core.userData.pulse?.material) {
        core.userData.pulse.material.color.setHex(recovery ? 0xfb923c : 0xffffff);
    }
}

function disposeGroupChildren(group) {
    const children = [...group.children];
    children.forEach(child => {
        group.remove(child);
        disposeObjectTree(child);
    });
}

function disposeObjectTree(object) {
    if (!object) {
        return;
    }

    object.traverse?.(child => {
        child.geometry?.dispose?.();
        if (Array.isArray(child.material)) {
            child.material.forEach(material => material?.dispose?.());
            return;
        }

        child.material?.dispose?.();
    });
}

function animateThree(state) {
    const now = performance.now();
    const delta = Math.min(0.04, (now - state.lastFrameTime) / 1000);
    state.lastFrameTime = now;

    const time = now / 1000;
    const recoveryPulse = state.data?.recovery ? 1 + Math.sin(time * 5.5) * 0.07 : 1 + Math.sin(time * 2.2) * 0.025;
    state.tunnelCore.scale.setScalar(recoveryPulse);
    state.tunnelCore.rotation.y += delta * 0.16;

    state.tunnelCore.userData.rings?.forEach((ring, index) => {
        ring.rotation.z += delta * ring.userData.spin * (0.65 + index * 0.28);
        ring.rotation.x += delta * ring.userData.spin * 0.06;
    });

    if (state.tunnelCore.userData.pulse) {
        const pulse = state.tunnelCore.userData.pulse;
        const pulseScale = 1.1 + (time % 1) * 2.4;
        pulse.scale.setScalar(pulseScale);
        pulse.material.opacity = Math.max(0, 0.5 - (time % 1) * 0.48);
    }

    state.serverCore.rotation.y = Math.sin(time * 0.7) * 0.22;
    state.serverCore.children.forEach((child, index) => {
        if (child.userData?.spin) {
            child.rotation.z += delta * child.userData.spin * (0.8 + index * 0.2);
        }
    });

    state.fieldGroup.rotation.y += delta * 0.015;
    state.pathGroup.children.forEach((node, index) => {
        node.position.y += Math.sin(time * 1.7 + index) * 0.0007;
        node.userData.rings?.forEach((ring, ringIndex) => {
            ring.rotation.z += delta * ring.userData.spin * (1.2 + ringIndex * 0.4);
        });
    });

    state.packets.forEach(packet => {
        const points = packet.userData.points;
        if (!points?.length) {
            return;
        }

        const raw = (time * packet.userData.speed + packet.userData.offset) % 1;
        const t = packet.userData.reverse ? 1 - raw : raw;
        const point = sampleArc(points, t);
        packet.position.copy(point);
        const scale = 0.13 + Math.sin(time * 8 + packet.userData.offset * 10) * 0.025;
        packet.scale.set(scale, scale, 1);
    });

    state.renderer.render(state.scene, state.camera);
    state.animationId = requestAnimationFrame(() => animateThree(state));
}

function handlePointerDown(state, event) {
    if (!state.raycaster || !state.camera) {
        return;
    }

    const rect = state.element.getBoundingClientRect();
    state.pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
    state.pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
    state.raycaster.setFromCamera(state.pointer, state.camera);

    const intersects = state.raycaster.intersectObjects(state.pathGroup.children, true);
    const hit = intersects
        .map(item => findPathGroup(item.object))
        .find(item => item && Number.isFinite(item.userData.pathId));

    if (hit) {
        state.dotNetReference?.invokeMethodAsync('SelectPathFromScene', hit.userData.pathId);
    }
}

function findPathGroup(object) {
    let cursor = object;
    while (cursor) {
        if (Number.isFinite(cursor.userData?.pathId)) {
            return cursor;
        }

        cursor = cursor.parent;
    }

    return null;
}

function sampleArc(points, t) {
    const scaled = Math.max(0, Math.min(0.999, t)) * (points.length - 1);
    const index = Math.floor(scaled);
    const localT = scaled - index;
    return points[index].clone().lerp(points[Math.min(points.length - 1, index + 1)], localT);
}

function getPathColor(path) {
    if (!path.up || path.status === 'down') {
        return 0xef4444;
    }

    if (path.cooldown || path.status === 'warn') {
        return 0xf59e0b;
    }

    if (path.anchor) {
        return 0xfb923c;
    }

    if (path.active) {
        return 0xf472b6;
    }

    return 0x22d3ee;
}

function getHealthColor(data) {
    const loss = Number(data?.loss || 0);
    const rtt = Number(data?.rtt || 0);
    if (!data?.running || loss >= 25 || rtt >= 300) {
        return 0xef4444;
    }

    if (loss >= 10 || rtt >= 180 || data?.recovery) {
        return 0xfb923c;
    }

    if (loss >= 2 || rtt >= 120) {
        return 0xfacc15;
    }

    return 0x22c55e;
}

function getBasicMaterial(cache, THREE, color, opacity) {
    const key = `basic-${color}-${opacity}`;
    if (!cache.has(key)) {
        cache.set(key, new THREE.MeshBasicMaterial({
            color,
            transparent: true,
            opacity,
            blending: THREE.AdditiveBlending,
            depthWrite: false,
            side: THREE.DoubleSide
        }));
    }

    return cache.get(key);
}

function getLineMaterial(cache, THREE, color, opacity) {
    const key = `line-${color}-${opacity}`;
    if (!cache.has(key)) {
        cache.set(key, new THREE.LineBasicMaterial({
            color,
            transparent: true,
            opacity,
            blending: THREE.AdditiveBlending,
            depthWrite: false
        }));
    }

    return cache.get(key);
}

function getEnergyMaterial(cache, THREE, color, opacity) {
    const key = `energy-${color}-${opacity}`;
    if (!cache.has(key)) {
        cache.set(key, new THREE.MeshBasicMaterial({
            color,
            transparent: true,
            opacity,
            blending: THREE.AdditiveBlending,
            depthWrite: false
        }));
    }

    return cache.get(key);
}

function setupFallbackCanvas(state) {
    const canvas = document.createElement('canvas');
    canvas.className = 'live-fallback-canvas';
    state.element.replaceChildren(canvas);
    state.fallbackCanvas = canvas;
    state.fallbackContext = canvas.getContext('2d');

    const resize = () => {
        const rect = state.element.getBoundingClientRect();
        const ratio = Math.min(window.devicePixelRatio || 1, 2);
        canvas.width = Math.max(1, Math.floor(rect.width * ratio));
        canvas.height = Math.max(1, Math.floor(rect.height * ratio));
        canvas.style.width = `${rect.width}px`;
        canvas.style.height = `${rect.height}px`;
        drawFallback(state);
    };

    state.resizeObserver = new ResizeObserver(resize);
    state.resizeObserver.observe(state.element);
    resize();
    animateFallback(state);
}

function animateFallback(state) {
    drawFallback(state);
    state.fallbackAnimationId = requestAnimationFrame(() => animateFallback(state));
}

function drawFallback(state) {
    const ctx = state.fallbackContext;
    const canvas = state.fallbackCanvas;
    if (!ctx || !canvas) {
        return;
    }

    const width = canvas.width;
    const height = canvas.height;
    const paths = Array.isArray(state.data?.paths) ? state.data.paths : [];
    const time = performance.now() / 1000;
    const centerX = width * 0.5;
    const centerY = height * 0.48;

    ctx.clearRect(0, 0, width, height);
    const gradient = ctx.createRadialGradient(centerX, centerY, 20, centerX, centerY, width * 0.7);
    gradient.addColorStop(0, 'rgba(34, 211, 238, 0.16)');
    gradient.addColorStop(1, 'rgba(2, 6, 23, 0)');
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, width, height);

    paths.forEach((path, index) => {
        const color = `#${getPathColor(path).toString(16).padStart(6, '0')}`;
        const pos = getFallbackEmitterPosition(index, Math.max(1, paths.length), width, height);

        ctx.save();
        ctx.globalCompositeOperation = 'lighter';
        ctx.strokeStyle = color;
        ctx.globalAlpha = path.active ? 0.72 : 0.22;
        ctx.lineWidth = path.active ? 5 : 2;
        ctx.beginPath();
        ctx.moveTo(pos.x, pos.y);
        ctx.bezierCurveTo(
            pos.x + (centerX - pos.x) * 0.35,
            pos.y - 90 + Math.sin(time + index) * 16,
            centerX + (pos.x - centerX) * 0.18,
            centerY - 75,
            centerX,
            centerY
        );
        ctx.stroke();

        ctx.globalAlpha = path.active ? 0.95 : 0.55;
        drawGlow(ctx, pos.x, pos.y, path.active ? 20 : 13, color);
        ctx.restore();
    });

    const coreColor = `#${getHealthColor(state.data).toString(16).padStart(6, '0')}`;
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    drawGlow(ctx, centerX, centerY, 54 + Math.sin(time * 3) * 5, coreColor);
    ctx.strokeStyle = coreColor;
    ctx.globalAlpha = 0.8;
    for (let i = 0; i < 3; i += 1) {
        ctx.beginPath();
        ctx.ellipse(centerX, centerY, 65 + i * 26, 22 + i * 10, time * 0.35 + i, 0, Math.PI * 2);
        ctx.stroke();
    }
    ctx.restore();
}

function getFallbackEmitterPosition(index, count, width, height) {
    const normalized = count === 1 ? 0.5 : index / (count - 1);
    const angle = Math.PI * 0.73 + normalized * Math.PI * 0.86;
    return {
        x: width * 0.5 + Math.cos(angle) * width * 0.36,
        y: height * 0.5 + Math.sin(angle) * height * 0.22
    };
}

function drawGlow(ctx, x, y, radius, color) {
    const gradient = ctx.createRadialGradient(x, y, 0, x, y, radius);
    gradient.addColorStop(0, color);
    gradient.addColorStop(0.35, `${color}88`);
    gradient.addColorStop(1, `${color}00`);
    ctx.fillStyle = gradient;
    ctx.beginPath();
    ctx.arc(x, y, radius, 0, Math.PI * 2);
    ctx.fill();
}
