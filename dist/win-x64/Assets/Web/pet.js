import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils } from './libs/three-vrm.module.js';

const stage = document.getElementById('stage');
const bubble = document.getElementById('bubble');
const textEl = document.getElementById('text');
const emotionEl = document.getElementById('emotion');

const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, premultipliedAlpha: false });
renderer.setClearColor(0x000000, 0);
renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.setAnimationLoop(null);
renderer.outputColorSpace = THREE.SRGBColorSpace;
stage.appendChild(renderer.domElement);
renderer.domElement.addEventListener('webglcontextlost', (event) => {
    event.preventDefault();
    console.error('Pet WebGL context lost');
});
renderer.domElement.addEventListener('webglcontextrestored', () => {
    console.info('Pet WebGL context restored');
});

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(30, window.innerWidth / window.innerHeight, 0.01, 100);

// 动漫模型贴图即最终颜色，灯光保持柔和
scene.add(new THREE.AmbientLight(0xffffff, 1.5));
const keyLight = new THREE.DirectionalLight(0xffffff, 0.95);
keyLight.position.set(0.6, 1.6, 1.6);
scene.add(keyLight);
const fillLight = new THREE.DirectionalLight(0xffd3e4, 0.4);
fillLight.position.set(-1.0, 0.9, -0.8);
scene.add(fillLight);

const clock = new THREE.Clock();

// 角色挂在 pivot 上，旋转都作用在 pivot，避免和 VRM 自身朝向互相覆盖
const pivot = new THREE.Group();
scene.add(pivot);

let vrm = null;
let baseYaw = 0;
let baseY = 0;
let modelHeight = 1.5;

let blinkPhase = -1;
let blinkTimer = 0;
let nextBlink = 2.5 + Math.random() * 4;

// ---------- 骨骼自动动作 ----------
const bones = {};
const baseQuats = {};
const parentWorld = {};
const parentWorldInv = {};
// 不同模型的 chest / upperChest 只会出现其中一个，两个都登记
const poseBones = ['hips', 'spine', 'chest', 'upperChest', 'neck', 'head',
    'leftShoulder', 'leftUpperArm', 'leftLowerArm', 'rightShoulder', 'rightUpperArm', 'rightLowerArm'];

function setupBones() {
    pivot.rotation.y = baseYaw;
    scene.updateMatrixWorld(true);
    for (const name of poseBones) {
        try {
            const node = vrm.humanoid.getNormalizedBoneNode(name);
            if (!node) continue;
            bones[name] = node;
            baseQuats[name] = node.quaternion.clone();
            // 骨骼局部坐标轴每个模型都不一样，偏移统一按角色自身坐标轴表达再换算回局部
            parentWorld[name] = node.parent
                ? node.parent.getWorldQuaternion(new THREE.Quaternion())
                : new THREE.Quaternion();
            parentWorldInv[name] = parentWorld[name].clone().invert();
        } catch (e) { /* 该模型没有这根骨骼 */ }
    }
}

const _euler = new THREE.Euler();
const _deltaQuat = new THREE.Quaternion();
const _localQuat = new THREE.Quaternion();

/** 叠加角色坐标轴方向的旋转（弧度：x 俯仰 / y 转头 / z 侧倾） */
function setBone(name, x, y, z) {
    const bone = bones[name];
    if (!bone) return;
    _euler.set(x, y, z);
    _deltaQuat.setFromEuler(_euler);
    _localQuat.copy(parentWorldInv[name]).multiply(_deltaQuat).multiply(parentWorld[name]).multiply(baseQuats[name]);
    bone.quaternion.copy(_localQuat);
}

const emotions = ['(｡•̀ᴗ-)✧', '(๑•̀ㅂ•́)و✧', '(*/ω＼*)', 'ヽ(✿ﾟ▽ﾟ)ノ', '(´・ω・`)', 'ヾ(≧▽≦*)o'];
const pick = (arr) => arr[Math.floor(Math.random() * arr.length)];

let bubbleTimer = null;
function petSay(text, emotion) {
    if (!text) return;
    textEl.textContent = text;
    emotionEl.textContent = emotion || pick(emotions);
    bubble.classList.add('show');
    if (bubbleTimer !== null) clearTimeout(bubbleTimer);
    bubbleTimer = setTimeout(() => bubble.classList.remove('show'), 4600);
}

function setExpression(name, weight) {
    try {
        if (vrm && vrm.expressionManager) vrm.expressionManager.setValue(name, weight);
    } catch (e) { /* 忽略不支持的表情 */ }
}

/** 按包围盒把角色完整取景到窗口里（留边距，避免顶满） */
function fitCameraTo(box) {
    const size = new THREE.Vector3();
    const center = new THREE.Vector3();
    box.getSize(size);
    box.getCenter(center);

    modelHeight = Math.max(size.y, 0.1);

    const fovRad = (camera.fov * Math.PI) / 180;
    const fitHeight = modelHeight / (2 * Math.tan(fovRad / 2));
    const fitWidth = size.x / (2 * Math.tan(fovRad / 2) * camera.aspect);
    const distance = Math.max(fitHeight, fitWidth) * 1.55;

    const targetY = center.y;
    camera.position.set(0, targetY + modelHeight * 0.06, distance);
    camera.near = Math.max(0.01, distance * 0.02);
    camera.far = distance * 20;
    camera.lookAt(0, targetY, 0);
    camera.updateProjectionMatrix();

    return targetY;
}

const requestedModel = new URLSearchParams(window.location.search).get('model');
const modelId = /^[a-f0-9]{32}$/.test(requestedModel || '') ? requestedModel : 'default';
const defaultModelUrl = 'https://avatar.local/avatar.vrm';
const modelUrl = modelId === 'default' ? defaultModelUrl : `https://avatar.local/Models/${modelId}.vrm`;
const manager = new THREE.LoadingManager();
manager.setURLModifier(url => {
    if (url === modelUrl || url === defaultModelUrl || /^(data:|blob:)/i.test(url)) return url;
    throw new Error('模型必须使用内嵌资源。');
});
const loader = new GLTFLoader(manager);
loader.register((parser) => new VRMLoaderPlugin(parser));

async function loadModel(url, isFallback = false) {
    let modelScene = null;
    try {
        const gltf = await loader.loadAsync(url);
        modelScene = gltf.scene;
        if (!gltf.userData.vrm) throw new Error('模型文件无效或格式不支持。');
        vrm = gltf.userData.vrm;

        try {
            VRMUtils.removeUnnecessaryVertices(gltf.scene);
            VRMUtils.combineSkeletons(gltf.scene);
        } catch (e) { /* 兼容旧版本 */ }

        // VRM 0.x 模型朝 -Z，需要额外转 180° 面向相机；不在场景里再调用 rotateVRM0
        const version = (vrm.meta && vrm.meta.metaVersion) ? String(vrm.meta.metaVersion) : '0';
        baseYaw = version === '0' ? Math.PI : 0;

        vrm.scene.traverse((o) => { o.frustumCulled = false; });
        pivot.add(vrm.scene);

        const box = new THREE.Box3().setFromObject(pivot);
        fitCameraTo(box);

        // 双脚落到画面原点
        vrm.scene.position.y -= box.min.y;
        baseY = vrm.scene.position.y;

        // Keep the model facing forward; pointer motion only moves its window.
        if (vrm.lookAt) vrm.lookAt.target = null;

        setupBones();
        applyPose(0);
        const message = isFallback ? '所选模型加载失败，已使用默认人物，请在设置中重新选择或导入。' : '人物模型已加载。';
        if (isFallback) petSay(message);
        post({ type: 'modelLoaded', id: isFallback ? 'default' : modelId, message });
    } catch (err) {
        vrm = null;
        if (modelScene) {
            pivot.remove(modelScene);
            VRMUtils.deepDispose(modelScene);
        }
        for (const map of [bones, baseQuats, parentWorld, parentWorldInv])
            for (const key of Object.keys(map)) delete map[key];
        pivot.rotation.set(0, 0, 0);
        const message = err?.message || String(err || '未知错误');
        console.error('Avatar load failed:', message);
        if (url !== defaultModelUrl && !isFallback) {
            await loadModel(defaultModelUrl, true);
        } else {
            const text = '模型加载失败，请在设置中导入有效的 VRM 模型。';
            petSay(text);
            post({ type: 'modelError', message: text });
        }
    }
}
loadModel(modelUrl);

// ---------------- 姿势系统：呼吸 + 重心晃动 + 视线游走 ----------------

let idleTime = 0;
let nextNodAt = 10 + Math.random() * 8;
let nodTime = -1;

let lookYaw = 0, lookPitch = 0;
let lookYawTo = 0, lookPitchTo = 0;
let nextLookAt = 3 + Math.random() * 4;

/** 相对“标准站姿”的骨骼旋转偏移（角色坐标轴，弧度） */
function poseOffsets(t) {
    const breath = Math.sin(t * 1.1);   // 约 0.18 Hz 的深呼吸
    const sway = Math.sin(t * 0.42);    // 缓慢的重心左右移
    const tilt = Math.sin(t * 0.29);

    let nod = 0;
    if (nodTime >= 0) {
        const p = Math.min(1, nodTime / 1.4);
        nod = Math.sin(p * Math.PI * 2); // 一次点头里的两下轻摆
    }

    return {
        hips: { x: 0.008 * breath, y: -0.022 * lookYaw - 0.012 * sway, z: 0.03 * sway },
        spine: { x: 0.014 * breath, y: 0.028 * sway, z: -0.018 * sway },
        chest: { x: -0.024 * breath, y: 0.09 * lookYaw, z: 0.012 * tilt },
        upperChest: { x: -0.024 * breath, y: 0.09 * lookYaw, z: 0.012 * tilt },
        neck: { x: 0.02 * nod + 0.008 * breath, y: 0.28 * lookYaw, z: 0.016 * tilt },
        head: { x: 0.05 * nod + 0.03 * lookPitch + 0.012 * breath, y: 0.6 * lookYaw + 0.02 * sway, z: 0.024 * tilt },
        // 模型导入是 T 字架，把手臂垂下来才不像手办；左右绕 Z 的符号相反
        leftShoulder: { x: 0, y: 0, z: -0.12 },
        rightShoulder: { x: 0, y: 0, z: 0.12 },
        leftUpperArm: { x: 0, y: 0, z: -1.15 + 0.02 * sway },
        rightUpperArm: { x: 0, y: 0, z: 1.15 + 0.02 * sway },
        leftLowerArm: { x: 0, y: 0, z: -0.3 },
        rightLowerArm: { x: 0, y: 0, z: 0.3 },
    };
}

function applyPose(t) {
    const offsets = poseOffsets(t);
    for (const name of poseBones) {
        const bone = bones[name];
        if (!bone) continue;
        const o = offsets[name] || { x: 0, y: 0, z: 0 };
        setBone(name, o.x, o.y, o.z);
    }
}

function updatePoses(dt, t) {
    idleTime += dt;

    if (idleTime >= nextLookAt) {
        idleTime = 0;
        nextLookAt = 4 + Math.random() * 7;
        lookYawTo = (Math.random() * 2 - 1) * 0.24;
        lookPitchTo = (Math.random() * 2 - 1) * 0.1;
        nextNodAt = Math.max(nextNodAt, idleTime + 8 + Math.random() * 8);
    }
    const kLook = 1 - Math.exp(-dt * 2.6);
    lookYaw += (lookYawTo - lookYaw) * kLook;
    lookPitch += (lookPitchTo - lookPitch) * kLook;

    if (nodTime >= 0) {
        nodTime += dt;
        if (nodTime >= 1.4) {
            nodTime = -1;
            nextNodAt = 10 + Math.random() * 8;
        }
    } else if (idleTime >= nextNodAt) {
        nodTime = 0;
    }

    applyPose(t);
}

let animationElapsed = 0;
function animate() {
    const frameDelta = Math.min(clock.getDelta(), 0.1);
    animationElapsed += frameDelta;
    // Render at a steady 30 fps to reduce WebView composition jitter.
    if (animationElapsed < 1 / 30) return;
    const dt = animationElapsed;
    animationElapsed = 0;
    if (vrm) {
        updatePoses(dt, clock.elapsedTime);

        // 朝向由头部/脊柱的转头动作体现，躯干只轻微跟随，避免整体旋转
        pivot.rotation.y = baseYaw + 0.05 * lookYaw;
        pivot.rotation.x = 0;
        vrm.scene.position.y = baseY;

        if (blinkPhase < 0) {
            blinkTimer += dt;
            if (blinkTimer > nextBlink) {
                blinkPhase = 0;
                blinkTimer = 0;
                nextBlink = 2.5 + Math.random() * 4.5;
            }
        } else {
            blinkPhase += dt / 0.15;
            const value = blinkPhase < 1 ? blinkPhase : Math.max(0, 2 - blinkPhase);
            setExpression('blink', Math.min(1, value));
            if (blinkPhase >= 2) {
                blinkPhase = -1;
                setExpression('blink', 0);
            }
        }

        vrm.update(dt);
    }

    renderer.render(scene, camera);
}
renderer.setAnimationLoop(animate);

// ---------------- 交互（整个窗口都可拖动） ----------------

let pointerDownAt = 0;
let pointerStartX = 0;
let pointerStartY = 0;
let pointerPressed = false;
let dragStarted = false;
window.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    pointerPressed = true;
    pointerDownAt = performance.now();
    pointerStartX = e.screenX;
    pointerStartY = e.screenY;
    dragStarted = false;
});
window.addEventListener('pointermove', (e) => {
    if (!pointerPressed || !(e.buttons & 1) || dragStarted) return;
    if (Math.abs(e.screenX - pointerStartX) + Math.abs(e.screenY - pointerStartY) < 5) return;
    dragStarted = true;
    post({ type: 'drag' });
});
function endPointer() {
    pointerPressed = false;
    dragStarted = false;
}
window.addEventListener('pointerup', (e) => {
    if (e.button === 0 && pointerPressed && !dragStarted && performance.now() - pointerDownAt < 250) onTap();
    endPointer();
});
window.addEventListener('pointercancel', endPointer);
window.addEventListener('blur', endPointer);

window.addEventListener('dblclick', (e) => e.preventDefault());

window.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    post({ type: 'menu' });
});

function onTap() {
    const now = performance.now();
    if (now - lastTapAt < 650) return;
    lastTapAt = now;
    lookYawTo = 0;
    lookPitchTo = 0;
    setExpression('happy', 0.2);
    if (happyTimer !== null) clearTimeout(happyTimer);
    happyTimer = setTimeout(() => {
        happyTimer = null;
        setExpression('happy', 0);
    }, 900);
    post({ type: 'tap' });
}

let lastTapAt = 0;
let happyTimer = null;

function post(message) {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message);
}

// C# → JS
if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', (event) => {
        const data = event.data;
        if (!data) return;
        if (typeof data === 'string') { petSay(data); return; }
        if (data.type === 'say') petSay(data.text, data.emotion);
    });
}

window.petSay = petSay;
post({ type: 'ready' });

window.addEventListener('resize', () => {
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(window.innerWidth, window.innerHeight);
});
