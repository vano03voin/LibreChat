# Offline dev rebuild using the already built, pinned upstream v1.10.4 image.
FROM semind-codeapi-api-base:1.10.4
COPY service/src /app/src
COPY service/tsconfig.json /app/tsconfig.json
COPY shared /shared
COPY packages/code/src /packages/code/src
RUN bun build ./src/api-server.ts --minify --outdir .build-api --target bun --external '@opentelemetry/*'
