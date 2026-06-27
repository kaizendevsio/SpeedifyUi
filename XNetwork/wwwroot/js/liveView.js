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
        lastFrameTime: performance.now()
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

    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, powerPreference: 'high-performance' });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    element.replaceChildren(renderer.domElement);

    const scene = new THREE.Scene();
    scene.fog = new THREE.FogExp2(0x0c111a, 0.025);

    const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 100);
    camera.position.set(0, 4.3, 12);
    camera.lookAt(0, 0, 0);

    const pathGroup = new THREE.Group();
    const beamGroup = new THREE.Group();
    const packetGroup = new THREE.Group();
    scene.add(pathGroup, beamGroup, packetGroup);

    const ambient = new THREE.AmbientLight(0x8fb7ff, 1.1);
    const key = new THREE.DirectionalLight(0xffffff, 2.5);
    key.position.set(3, 7, 8);
    const rim = new THREE.PointLight(0x22d3ee, 75, 18);
    rim.position.set(-4, 2, 3);
    scene.add(ambient, key, rim);

    const floor = new THREE.Mesh(
        new THREE.CircleGeometry(8.5, 96),
        new THREE.MeshBasicMaterial({
            color: 0x102033,
            transparent: true,
            opacity: 0.34,
            depthWrite: false
        }));
    floor.rotation.x = -Math.PI / 2;
    floor.position.y = -1.9;
    scene.add(floor);

    const tunnelCore = new THREE.Mesh(
        new THREE.IcosahedronGeometry(1.05, 2),
        new THREE.MeshStandardMaterial({
            color: 0x22c55e,
            emissive: 0x0f5132,
            metalness: 0.28,
            roughness: 0.24
        }));
    tunnelCore.name = 'XBond core';
    tunnelCore.position.set(0, 0, 0);
    scene.add(tunnelCore);

    const ringMaterial = new THREE.MeshBasicMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.32,
        side: THREE.DoubleSide,
        depthWrite: false
    });
    const ring = new THREE.Mesh(new THREE.TorusGeometry(1.55, 0.025, 12, 128), ringMaterial);
    ring.rotation.x = Math.PI / 2;
    tunnelCore.add(ring);
    tunnelCore.userData.ring = ring;

    const serverCore = new THREE.Mesh(
        new THREE.BoxGeometry(1.15, 0.78, 0.78),
        new THREE.MeshStandardMaterial({
            color: 0x3794ff,
            emissive: 0x0a2d52,
            metalness: 0.5,
            roughness: 0.3
        }));
    serverCore.position.set(4.6, 0.2, -1.2);
    scene.add(serverCore);

    state.THREE = THREE;
    state.renderer = renderer;
    state.scene = scene;
    state.camera = camera;
    state.raycaster = new THREE.Raycaster();
    state.pointer = new THREE.Vector2();
    state.pathGroup = pathGroup;
    state.beamGroup = beamGroup;
    state.packetGroup = packetGroup;
    state.tunnelCore = tunnelCore;
    state.serverCore = serverCore;

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
    const activeCount = Math.max(1, paths.filter(path => path.active).length);

    disposeGroupChildren(state.pathGroup);
    disposeGroupChildren(state.beamGroup);
    disposeGroupChildren(state.packetGroup);
    state.nodeMeshes.clear();
    state.packets = [];

    const nodeMaterialCache = new Map();
    const beamMaterialCache = new Map();
    const radius = getResponsiveRadius(state.element);

    paths.forEach((path, index) => {
        const angle = paths.length <= 1
            ? Math.PI
            : Math.PI * 0.88 + (index / Math.max(1, paths.length - 1)) * Math.PI * 1.24;
        const position = new THREE.Vector3(
            Math.cos(angle) * radius - 0.6,
            Math.sin(index * 1.31) * 0.42,
            Math.sin(angle) * radius * 0.36
        );

        const color = getPathColor(path);
        const selected = Number(path.id) === selectedPathId;
        const scale = selected ? 1.18 : 1;
        const node = new THREE.Group();
        node.position.copy(position);
        node.userData.pathId = Number(path.id);

        const material = getCachedStandardMaterial(nodeMaterialCache, THREE, color, path.active ? 0.48 : 0.18);
        const core = new THREE.Mesh(new THREE.SphereGeometry(0.34 * scale, 24, 16), material);
        node.add(core);

        const bars = Math.max(0, Math.min(4, Number(path.signal) || 0));
        for (let i = 0; i < 4; i += 1) {
            const barHeight = 0.18 + i * 0.12;
            const bar = new THREE.Mesh(
                new THREE.BoxGeometry(0.08, barHeight, 0.08),
                getCachedBasicMaterial(nodeMaterialCache, THREE, i < bars ? color : 0x334155, i < bars ? 0.9 : 0.45)
            );
            bar.position.set(-0.42 + i * 0.13, -0.44 + barHeight / 2, 0.02);
            node.add(bar);
        }

        if (selected) {
            const halo = new THREE.Mesh(
                new THREE.TorusGeometry(0.54, 0.018, 10, 64),
                getCachedBasicMaterial(nodeMaterialCache, THREE, color, 0.72)
            );
            halo.rotation.x = Math.PI / 2;
            node.add(halo);
        }

        state.pathGroup.add(node);
        state.nodeMeshes.set(Number(path.id), node);

        const target = path.active ? state.tunnelCore.position : state.serverCore.position;
        const opacity = path.active ? (path.anchor ? 0.78 : 0.58) : 0.22;
        const beamMaterial = getCachedLineMaterial(beamMaterialCache, THREE, color, opacity);
        const points = makeArcPoints(position, target, path.anchor ? 0.85 : 0.48);
        const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(points), beamMaterial);
        state.beamGroup.add(line);

        if (path.active) {
            const packetCount = Math.min(5, Math.max(2, Math.ceil(activeCount + Number(path.down || 0) + Number(path.upMbps || 0))));
            for (let packetIndex = 0; packetIndex < packetCount; packetIndex += 1) {
                const packet = new THREE.Mesh(
                    new THREE.SphereGeometry(path.anchor ? 0.055 : 0.045, 12, 8),
                    getCachedBasicMaterial(nodeMaterialCache, THREE, color, path.anchor ? 0.95 : 0.75)
                );
                packet.userData = {
                    points,
                    offset: packetIndex / packetCount,
                    speed: path.anchor ? 0.23 : 0.18,
                    reverse: packetIndex % 2 === 1
                };
                state.packetGroup.add(packet);
                state.packets.push(packet);
            }
        }
    });

    const healthColor = getHealthColor(state.data);
    state.tunnelCore.material.color.setHex(healthColor);
    state.tunnelCore.material.emissive.setHex(darkenHex(healthColor, 0.26));
    state.tunnelCore.userData.ring.material.color.setHex(state.data?.recovery ? 0xfb923c : 0x22d3ee);
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
    const recoveryPulse = state.data?.recovery ? 1 + Math.sin(time * 5.2) * 0.08 : 1 + Math.sin(time * 2.1) * 0.025;
    state.tunnelCore.rotation.y += delta * 0.45;
    state.tunnelCore.rotation.x = Math.sin(time * 0.7) * 0.06;
    state.tunnelCore.scale.setScalar(recoveryPulse);
    state.tunnelCore.userData.ring.rotation.z += delta * (state.data?.recovery ? 1.9 : 0.7);
    state.serverCore.rotation.y = Math.sin(time * 0.5) * 0.16;

    state.pathGroup.children.forEach((node, index) => {
        node.rotation.y += delta * 0.5;
        node.position.y += Math.sin(time * 1.6 + index) * 0.0009;
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

function makeArcPoints(from, to, lift) {
    const points = [];
    for (let i = 0; i <= 32; i += 1) {
        const t = i / 32;
        const point = from.clone().lerp(to, t);
        point.y += Math.sin(t * Math.PI) * lift;
        points.push(point);
    }

    return points;
}

function sampleArc(points, t) {
    const scaled = Math.max(0, Math.min(0.999, t)) * (points.length - 1);
    const index = Math.floor(scaled);
    const localT = scaled - index;
    return points[index].clone().lerp(points[Math.min(points.length - 1, index + 1)], localT);
}

function getResponsiveRadius(element) {
    const width = element.getBoundingClientRect().width;
    return width < 560 ? 3.7 : 4.8;
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

function darkenHex(hex, factor) {
    const r = Math.floor(((hex >> 16) & 255) * factor);
    const g = Math.floor(((hex >> 8) & 255) * factor);
    const b = Math.floor((hex & 255) * factor);
    return (r << 16) | (g << 8) | b;
}

function getCachedStandardMaterial(cache, THREE, color, emissiveFactor) {
    const key = `standard-${color}-${emissiveFactor}`;
    if (!cache.has(key)) {
        cache.set(key, new THREE.MeshStandardMaterial({
            color,
            emissive: darkenHex(color, emissiveFactor),
            metalness: 0.28,
            roughness: 0.22
        }));
    }

    return cache.get(key);
}

function getCachedBasicMaterial(cache, THREE, color, opacity) {
    const key = `basic-${color}-${opacity}`;
    if (!cache.has(key)) {
        cache.set(key, new THREE.MeshBasicMaterial({
            color,
            transparent: opacity < 1,
            opacity,
            depthWrite: opacity >= 1
        }));
    }

    return cache.get(key);
}

function getCachedLineMaterial(cache, THREE, color, opacity) {
    const key = `${color}-${opacity}`;
    if (!cache.has(key)) {
        cache.set(key, new THREE.LineBasicMaterial({
            color,
            transparent: true,
            opacity,
            linewidth: 2
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
        canvas.width = Math.max(1, Math.floor(rect.width * Math.min(window.devicePixelRatio || 1, 2)));
        canvas.height = Math.max(1, Math.floor(rect.height * Math.min(window.devicePixelRatio || 1, 2)));
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
        const angle = Math.PI * 0.8 + index / Math.max(1, paths.length - 1) * Math.PI * 1.4;
        const x = centerX + Math.cos(angle) * width * 0.34;
        const y = centerY + Math.sin(angle) * height * 0.24;
        const color = `#${getPathColor(path).toString(16).padStart(6, '0')}`;

        ctx.strokeStyle = color;
        ctx.globalAlpha = path.active ? 0.75 : 0.25;
        ctx.lineWidth = path.active ? 3 : 1.5;
        ctx.beginPath();
        ctx.moveTo(x, y);
        ctx.quadraticCurveTo(centerX, centerY - 70, centerX, centerY);
        ctx.stroke();

        ctx.globalAlpha = 1;
        ctx.fillStyle = color;
        ctx.beginPath();
        ctx.arc(x, y + Math.sin(time * 2 + index) * 3, path.active ? 10 : 7, 0, Math.PI * 2);
        ctx.fill();
    });

    ctx.fillStyle = `#${getHealthColor(state.data).toString(16).padStart(6, '0')}`;
    ctx.beginPath();
    ctx.arc(centerX, centerY, 26 + Math.sin(time * 3) * 2, 0, Math.PI * 2);
    ctx.fill();
    ctx.globalAlpha = 1;
}
