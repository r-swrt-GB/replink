#!/bin/bash

# Seed Exercises API with sample data
# Run this after the system is up and you have a valid JWT token

if [ -z "$1" ]; then
    echo "Usage: ./seed-exercises.sh <JWT_TOKEN>"
    exit 1
fi

TOKEN=$1
API_BASE="http://localhost:8000"

echo "Seeding Exercises..."

# Strength exercises
curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Barbell Bench Press",
    "description": "Lie on bench, lower barbell to chest, press up",
    "category": "strength",
    "muscleGroup": "chest",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Barbell Squat",
    "description": "Bar on shoulders, squat down keeping chest up, drive through heels",
    "category": "strength",
    "muscleGroup": "legs",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Deadlift",
    "description": "Lift barbell from ground to standing position with neutral spine",
    "category": "strength",
    "muscleGroup": "back",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Pull-ups",
    "description": "Hang from bar, pull body up until chin over bar",
    "category": "strength",
    "muscleGroup": "back",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Overhead Press",
    "description": "Press barbell from shoulders to overhead position",
    "category": "strength",
    "muscleGroup": "shoulders",
    "mediaUrl": ""
  }'

echo ""

# Cardio exercises
curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Running",
    "description": "Outdoor or treadmill running at various intensities",
    "category": "cardio",
    "muscleGroup": "legs",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Rowing Machine",
    "description": "Full body cardio exercise on rowing ergometer",
    "category": "cardio",
    "muscleGroup": "full body",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Burpees",
    "description": "Drop to plank, push-up, jump feet forward, jump up",
    "category": "cardio",
    "muscleGroup": "full body",
    "mediaUrl": ""
  }'

echo ""

# Mobility exercises
curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Hip Flexor Stretch",
    "description": "Lunge position stretching hip flexors",
    "category": "mobility",
    "muscleGroup": "hips",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Shoulder Dislocations",
    "description": "Pass band or stick overhead and behind back to improve shoulder mobility",
    "category": "mobility",
    "muscleGroup": "shoulders",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Goblet Squat",
    "description": "Hold weight at chest, squat down maintaining upright torso",
    "category": "strength",
    "muscleGroup": "legs",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Plank",
    "description": "Hold push-up position on forearms, maintaining neutral spine",
    "category": "strength",
    "muscleGroup": "core",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Dumbbell Rows",
    "description": "Single arm rowing motion with dumbbell",
    "category": "strength",
    "muscleGroup": "back",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Lunges",
    "description": "Step forward into lunge position, alternate legs",
    "category": "strength",
    "muscleGroup": "legs",
    "mediaUrl": ""
  }'

echo ""

curl -X POST "$API_BASE/api/exercises" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Box Jumps",
    "description": "Jump onto elevated platform, step down",
    "category": "cardio",
    "muscleGroup": "legs",
    "mediaUrl": ""
  }'

echo ""
echo "Exercises seeded successfully!"
