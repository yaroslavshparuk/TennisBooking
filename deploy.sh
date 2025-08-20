#!/bin/bash

# Deployment script for TennisBooking application
# This script builds a Docker image, pushes it to Docker Hub, and deploys it to a remote server

set -e  # Exit on any error

# Configuration
DOCKER_IMAGE="shparuk/tennisbooking"
DOCKER_TAG="latest"
CONTAINER_NAME="tennisbooking"
REMOTE_HOST="192.168.50.182"  # Set your remote host IP/hostname here
REMOTE_USER="yaroslav"  # Set your remote username here

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

echo_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

echo_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Check if required variables are set
if [ -z "$REMOTE_HOST" ] || [ -z "$REMOTE_USER" ]; then
    echo_error "Please set REMOTE_HOST and REMOTE_USER variables in the script"
    exit 1
fi

# Step 1: Build Docker image
echo_info "Building Docker image..."
cd "$(dirname "$0")/src/TennisBooking"
docker build -t $DOCKER_IMAGE:$DOCKER_TAG .

if [ $? -eq 0 ]; then
    echo_info "Docker image built successfully"
else
    echo_error "Failed to build Docker image"
    exit 1
fi

# Step 2: Push to Docker Hub
echo_info "Pushing image to Docker Hub..."
docker push $DOCKER_IMAGE:$DOCKER_TAG

if [ $? -eq 0 ]; then
    echo_info "Image pushed to Docker Hub successfully"
else
    echo_error "Failed to push image to Docker Hub"
    exit 1
fi

# Step 3: Deploy to remote server
echo_info "Deploying to remote server $REMOTE_HOST..."

# Prepare SSH command
SSH_CMD="ssh"
if [ -n "$SSH_KEY_PATH" ]; then
    SSH_CMD="$SSH_CMD -i $SSH_KEY_PATH"
fi
SSH_CMD="$SSH_CMD $REMOTE_USER@$REMOTE_HOST"

# Create deployment commands for remote server
REMOTE_COMMANDS=$(cat << 'EOF'
#!/bin/bash
set -e

DOCKER_IMAGE="shparuk/tennisbooking"
DOCKER_TAG="latest"
CONTAINER_NAME="tennisbooking"

echo "Pulling latest image from Docker Hub..."
docker pull $DOCKER_IMAGE:$DOCKER_TAG

echo "Stopping and removing existing container if it exists..."
docker stop $CONTAINER_NAME 2>/dev/null || true
docker rm $CONTAINER_NAME 2>/dev/null || true

echo "Creating and starting new container..."
docker run -d \
    --name $CONTAINER_NAME \
    --restart unless-stopped \
    -p 80:5000 \
    -e "ConnectionStrings__Default=Host=192.168.50.228;Port=5432;Database=tennis_db;Username=postgres;Password=bla;Pooling=true;Minimum Pool Size=0;Maximum Pool Size=20;Timeout=15;" \
    -e "Hangfire__DashboardUser=admin" \
    -e "Hangfire__DashboardPass=pass@word" \
    -e "OpenTelemetry__ServiceName=TennisBooking" \
    -e "OpenTelemetry__Endpoint=http://192.168.50.116:4317" \
    $DOCKER_IMAGE:$DOCKER_TAG

echo "Checking container status..."
docker ps | grep $CONTAINER_NAME

echo "Container logs (last 20 lines):"
docker logs --tail 20 $CONTAINER_NAME

echo "Deployment completed successfully!"
EOF
)

# Execute commands on remote server
echo_info "Executing deployment commands on remote server..."
echo "$REMOTE_COMMANDS" | $SSH_CMD 'bash -s'

if [ $? -eq 0 ]; then
    echo_info "Deployment completed successfully!"
    echo_info "Application should be available at http://$REMOTE_HOST"
else
    echo_error "Deployment failed"
    exit 1
fi

echo_info "Deployment script finished"
