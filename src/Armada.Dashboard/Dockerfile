# Exact patch tags. Bump a tag on purpose.
# --- Build stage ---
FROM node:22.23.4-alpine3.24 AS build
WORKDIR /app
COPY src/Armada.Dashboard/package.json src/Armada.Dashboard/package-lock.json* ./
RUN npm ci
COPY src/Armada.Dashboard/ .
RUN npm run build

# --- Runtime stage ---
FROM nginx:1.31.8-alpine3.24
COPY --from=build /app/dist /usr/share/nginx/html
COPY src/Armada.Dashboard/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
