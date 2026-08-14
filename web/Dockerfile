# Build
FROM node:22-alpine AS build
WORKDIR /source

# Lockfile first, so an edit to a component does not reinstall node_modules.
COPY web/package.json web/package-lock.json ./
RUN npm ci

COPY web/ ./
RUN npm run build

# Serve
FROM nginx:alpine
COPY --from=build /source/dist/web/browser /usr/share/nginx/html
COPY web/nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80
