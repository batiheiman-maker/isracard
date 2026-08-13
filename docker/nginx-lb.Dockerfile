# Build context: repo root. Combines the built frontend with the multi-replica LB config, so
# the docker-compose distributed proof is one same-origin entrypoint (no CORS needed).
FROM node:24-alpine AS build
WORKDIR /app
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ .
RUN npm run build

FROM nginx:1.27-alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY docker/nginx-lb.conf /etc/nginx/conf.d/default.conf
COPY docker/nginx-lb-main.conf /etc/nginx/nginx.conf
EXPOSE 80
