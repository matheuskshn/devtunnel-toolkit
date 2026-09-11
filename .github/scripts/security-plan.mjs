import { appendFileSync, readFileSync } from "node:fs";
import { selectImages, selectForEvent } from "./image-changes.mjs";
const selection =
  process.env.SECURITY_FULL_SUITE === "true"
    ? selectImages([], true)
    : selectForEvent(
        JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, "utf8")),
        process.env.GITHUB_EVENT_NAME,
        process.env.GITHUB_REF,
      );
const include = selection.matrix.include;
if (selection.hub)
  include.push({
    image: "devtunnel-toolkit-hub",
    context: "images/hub",
    dockerfile: "images/hub/Dockerfile",
  });
const matrix = {
  include: include.flatMap((image) =>
    ["linux/amd64", "linux/arm64"].map((platform) => ({ ...image, platform })),
  ),
};
appendFileSync(
  process.env.GITHUB_OUTPUT,
  `matrix=${JSON.stringify(matrix)}\nany=${include.length > 0}\n`,
);
