// src/VrHomeSense.Server/index.js
import express from "express";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

//
// 設定ロード: appsettings.Local.json → 環境変数 HUB_API_KEY
//
let localConfig = {};
try {
  const json = fs.readFileSync(
    path.join(__dirname, "appsettings.Local.json"),
    "utf8"
  );
  localConfig = JSON.parse(json);
  console.log("[Config] appsettings.Local.json loaded");
} catch {
  console.log("[Config] appsettings.Local.json not found, use env only");
}

function getApiKey() {
  if (localConfig.Hub?.ApiKey) return localConfig.Hub.ApiKey;
  if (process.env.HUB_API_KEY) return process.env.HUB_API_KEY;
  return "";
}

const HUB_API_KEY = getApiKey();

//
// Express 本体
//
const app = express();
app.use(express.json());

// 部屋ごとの状態をメモリに保持
/** @type {Map<string, any>} */
const roomStore = new Map();

// POST /api/room/:roomId  ← Hub からのアップロード用
app.post("/api/room/:roomId", (req, res) => {
  const roomId = req.params.roomId;   // ← 呼び出し側が自由に決める

  // --- API キーチェック ---
  const apiKey = req.header("X-Api-Key");
  if (!HUB_API_KEY || apiKey !== HUB_API_KEY) {
    console.warn(`[WARN] Invalid POST from ${req.ip}`);
    return res.sendStatus(401);
  }

  const {
    deviceAddress = "",
    co2Ppm,
    temperature,
    humidity,
    sourceTimestamp
  } = req.body ?? {};

  // --- バリデーション ---
  if (
    typeof co2Ppm !== "number" ||
    typeof temperature !== "number" ||
    typeof humidity !== "number" ||
    co2Ppm < 0 || co2Ppm > 10000 ||
    temperature < -50 || temperature > 60 ||
    humidity < 0 || humidity > 100
  ) {
    return res.status(400).send("invalid range");
  }

  const state = {
    roomId,
    deviceAddress,
    co2Ppm,
    temperature,
    humidity,
    sourceTime: sourceTimestamp ?? null,
    lastUpdated: new Date().toISOString()
  };

  roomStore.set(roomId, state);
  console.log(
    `[UPDATE] ${roomId} CO2=${co2Ppm} temp=${temperature} hum=${humidity}`
  );

  return res.sendStatus(204);
});

// GET /api/room/:roomId  ← VRChat / ブラウザ用
app.get("/api/room/:roomId", (req, res) => {
  const roomId = req.params.roomId;
  const state = roomStore.get(roomId);
  console.log("[GET]", roomId, state ? "HIT" : "MISS");
  if (!state) return res.sendStatus(404);
  // 例: "693,21.8,42" （CO2, 温度, 湿度）
  const line = `${state.co2Ppm},${state.temperature},${state.humidity}`;

  res.setHeader("Content-Type", "text/plain; charset=utf-8");
  return res.send(line);
});

// Render では PORT が環境変数で渡される
const port = process.env.PORT || 3000;
app.listen(port, () => {
  console.log(`VrHomeSense.Server listening on port ${port}`);
});
