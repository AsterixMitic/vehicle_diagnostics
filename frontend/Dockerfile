# Build with Node, serve with nginx (design D9). Built from this folder:
#   docker build -t vehicle-dashboard frontend/
FROM node:24-alpine AS build
WORKDIR /src
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY . .
RUN pnpm run build

FROM nginx:stable-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/vehicle-dashboard/browser /usr/share/nginx/html
