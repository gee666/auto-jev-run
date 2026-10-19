"""Classify changing articles with the same question/options prefix."""
import os, json, urllib.request
questions = {'topic': {'type': 'choice', 'instructions': 'Select the main news topic.', 'criteria': {
    'technology': 'Software, AI, computing and cybersecurity.',
    'business': 'Company earnings, mergers and business deals.',
    'sports': 'Athletic competitions and match results.',
}}}
articles = [
    'Developers released a patch for a database security vulnerability.',
    'The retailer reported higher quarterly profit and announced an acquisition.',
    'The team won the championship final in extra time.',
]
for article in articles:
    body = {'model': 'jev-latest', 'state': article, 'questions': questions}
    request = urllib.request.Request('http://localhost:8765/v1/systemone', data=json.dumps(body).encode(), headers={
        'Authorization': 'Bearer ' + os.environ['TYPESAFE_API_KEY'], 'Content-Type': 'application/json', 'X-AutoJev-Prefix-Cache': 'true',
    })
    with urllib.request.urlopen(request, timeout=1200) as response:
        result = json.load(response)
        print(result['answers']['topic']['choice'], 'cached tokens:', response.headers.get('X-AutoJev-Cached-Tokens'))
