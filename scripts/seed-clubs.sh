#!/bin/bash

# Seed Clubs API with sample data
# Run this after the system is up and you have a valid JWT token

if [ -z "$1" ]; then
    echo "Usage: ./seed-clubs.sh <JWT_TOKEN>"
    exit 1
fi

TOKEN=$1
API_BASE="http://localhost:8000"

echo "Seeding Clubs..."

# Create sample clubs
curl -X POST "$API_BASE/api/clubs" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Gold Gym Downtown",
    "description": "Premier fitness center in the heart of downtown with state-of-the-art equipment",
    "location": "123 Main St, Downtown"
  }'

echo ""

curl -X POST "$API_BASE/api/clubs" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "CrossFit Revolution",
    "description": "High-intensity functional fitness training facility",
    "location": "456 Fitness Ave, Westside"
  }'

echo ""

curl -X POST "$API_BASE/api/clubs" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Yoga & Wellness Studio",
    "description": "Peaceful studio offering yoga, pilates, and meditation classes",
    "location": "789 Zen Blvd, Eastside"
  }'

echo ""

curl -X POST "$API_BASE/api/clubs" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Powerhouse Athletics",
    "description": "Olympic weightlifting and strength training gym",
    "location": "321 Barbell Lane, Industrial District"
  }'

echo ""

curl -X POST "$API_BASE/api/clubs" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "24/7 Fitness Hub",
    "description": "Round-the-clock access fitness facility with personal training",
    "location": "555 Always Open Way, Midtown"
  }'

echo ""
echo "Clubs seeded successfully!"
